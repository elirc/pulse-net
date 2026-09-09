"""Exercise adoption, upgrade, restoration and startup using only disposable databases."""
import base64
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import time
import urllib.request
import uuid

root = Path(__file__).resolve().parents[1]
work = root / "tmp" / ("upgrade-rehearsal-" + uuid.uuid4().hex)
work.mkdir(parents=True)
dotnet = os.environ.get("DOTNET_EXE") or shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet.exe")
app = root / "src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll"
if not app.exists():
    raise SystemExit("Build src/Pulse.Api in Debug before this rehearsal.")
env = os.environ.copy()
env["DOTNET_PROCESSOR_COUNT"] = "2"
env["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true"
env["Logging__LogLevel__Default"] = "Warning"
evidence = []

def record(value):
    evidence.append(value)
    print(json.dumps(value), flush=True)

def command(action, database, expected):
    result = subprocess.run([dotnet, str(app), "db", action, "--database", str(database)],
                            cwd=root, env=env, capture_output=True, text=True, timeout=180)
    value = json.loads(result.stdout)
    assert value["state"] == expected, (action, result.returncode, value)
    record({"action": action, "database": database.name, "state": value["state"], "exitCode": result.returncode})

def start_probe(database, label, required_key_available=None):
    local = env.copy()
    local["ConnectionStrings__Pulse"] = f"Data Source={database};Pooling=False"
    # The isolated fixture owns version 77. This is public test material, never a deployment secret.
    if required_key_available is True:
        local["Erasure__Keys__77"] = base64.b64encode(b"rehearsal-only-key-version-77!!!!").decode("ascii")
    elif required_key_available is False:
        local.pop("Erasure__Keys__77", None)
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    local["ASPNETCORE_URLS"] = f"http://127.0.0.1:{port}"
    log_path = work / (label + ".log")
    with log_path.open("w", encoding="utf-8") as log:
        server = subprocess.Popen([dotnet, str(app)], cwd=root / "src/Pulse.Api", env=local, stdout=log, stderr=log)
        try:
            if required_key_available is False:
                code = server.wait(timeout=180)
                assert code != 0
                assert "Required suppression key version 77 is unavailable" in log_path.read_text(encoding="utf-8")
                record({"action": label, "state": "refused-missing-required-key"})
                return
            deadline = time.monotonic() + 180
            while True:
                if server.poll() is not None:
                    raise RuntimeError(f"Rehearsal API exited; inspect {log_path}")
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=3) as response:
                        health = json.load(response)
                    assert health["checks"]["database"] == "ok"
                    record({"action": label, "status": health["status"]})
                    return
                except (OSError, TimeoutError):
                    if time.monotonic() > deadline:
                        raise
                    time.sleep(0.5)
        finally:
            if server.poll() is None:
                server.terminate()
            server.wait(timeout=30)

legacy = work / "legacy-original.db"
with sqlite3.connect(legacy) as db:
    db.executescript((root / "tests/Pulse.Tests/Fixtures/legacy-schema.sql").read_text(encoding="utf-8"))
    db.execute("INSERT INTO Projects (Id,Name,ApiKey,ReadKey,CreatedAt) VALUES (?,?,?,?,?)",
               ("00000000-0000-0000-0000-000000000001", "Preservation rehearsal", "pk_rehearsal", "rk_rehearsal", 639078624000000000))
    db.execute("INSERT INTO ProjectMemberships (Id,ProjectId,UserId,CreatedAt) VALUES (?,?,?,?)",
               ("00000000-0000-0000-0000-000000000002", "00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000003", 639078624000000000))
restored = work / "restored-copy.db"
shutil.copy2(legacy, restored)
command("status", restored, "legacy")
command("adopt-legacy", restored, "pending")
command("status", restored, "pending")
command("upgrade", restored, "current")
command("upgrade", restored, "current")
command("status", legacy, "legacy")
with sqlite3.connect(restored) as db:
    assert db.execute("SELECT Name FROM Projects").fetchone()[0] == "Preservation rehearsal"
    assert db.execute("SELECT Role FROM ProjectMemberships").fetchone()[0] == "Admin"
    assert db.execute("SELECT Enabled FROM ProjectRetentionPolicies").fetchone()[0] == 0
    assert db.execute("SELECT COUNT(*) FROM AlertRules").fetchone()[0] == 0
    record({"action": "preserved-backfills", "migrationCount": db.execute('SELECT COUNT(*) FROM "__EFMigrationsHistory"').fetchone()[0]})
fresh = work / "fresh.db"
command("status", fresh, "missing")
assert not fresh.exists()
command("upgrade", fresh, "current")
start_probe(restored, "start-after-upgrade")
with sqlite3.connect(restored) as db:
    db.execute("INSERT INTO IdentitySuppressions (ProjectId,KeyVersion,Fingerprint,CreatedAt) VALUES (?,?,?,?)",
               ("00000000-0000-0000-0000-000000000001", 77, "a" * 64, 639078624000000000))
start_probe(restored, "missing-key-startup", required_key_available=False)
start_probe(restored, "restored-key-startup", required_key_available=True)
(work / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
print(f"Rehearsal evidence: {work / 'evidence.json'}", flush=True)
