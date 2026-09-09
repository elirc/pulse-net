# Upgrade and recover a Pulse SQLite database

These commands are implemented for SR-01. Check the
[implementation journal](../../astradocs/bootcamp/journal.md) for current test
evidence; do not infer release readiness from the existence of this runbook.

## Build and identify the exact file

Run from the repository root after building:

```powershell
dotnet build
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db status --database src/Pulse.Api/pulse.db
```

The database argument is mandatory and resolves relative to the command's
working directory. Output contains the resolved path, state, applied migrations,
pending migrations, and diagnostic detail. It does not contain project keys or
application records. The command exits before creating the HTTP host or workers.
Use the actual configured path if your installation overrides the default.

`status` opens an existing file read-only. A missing file reports `missing`
and exits nonzero without creating it. `unknown` and `unsupported` also exit
nonzero. `legacy` and `pending` are recognized states requiring maintenance;
`current` is the state accepted by ordinary startup.

## Stop writers and make a recoverable backup

1. Stop every Pulse API/worker process using this file, including older binaries
   and seed processes. Close database inspection tools holding connections too.
2. Record the application version and resolved database path. Ensure you have
   the application version that understands the backup's schema.
3. Make a consistent SQLite backup. If every connection closed cleanly and no
   WAL/journal sidecar remains, a file copy of the database is sufficient. If a
   sidecar remains or shutdown was interrupted, use SQLite's backup API or a
   database backup tool that understands WAL; copying only the main file can
   miss committed changes. Do not delete sidecars to make the check pass.
4. Restore the backup to a **different** disposable path. Run `db status` against
   that restored file. Rehearse the applicable adoption and upgrade below there
   and verify representative IDs, timestamps, payloads, and keys through trusted
   local checks. Avoid printing credentials into shared logs.
5. Preserve the original backup and application version before modifying the
   real file. A backup that cannot be restored is not recovery evidence.

The automated preservation and separate-path restoration examples are in
[DatabaseUpgradeTests](../../tests/Pulse.Tests/Infrastructure/DatabaseUpgradeTests.cs).
Their file-copy fixture has one explicitly closed connection and pooling disabled.
Do not generalize that setup to an active production database.

For a complete disposable command/startup rehearsal, build the Debug API and run
`python scripts/verify-upgrade-rehearsal.py` from the repository root. It creates
its own directory under `tmp`, preserves an original legacy fixture, upgrades a
separate copy through all current migrations, checks backfills, and starts only
its own temporary API processes. It also tests missing/restored suppression-key
startup behavior with public test material. The printed evidence path contains
JSON results and local server logs. Set `DOTNET_EXE` if the SDK is not on PATH.

## Choose the indicated path

For a fresh installation or an empty database:

```powershell
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db upgrade --database src/Pulse.Api/pulse.db
```

For a recognized legacy database, first adopt its verified baseline, then upgrade:

```powershell
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db adopt-legacy --database src/Pulse.Api/pulse.db
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db upgrade --database src/Pulse.Api/pulse.db
```

For supported history with pending migrations, run only `db upgrade`.
For `current`, repeat upgrade is a no-op. Repeated adoption of a supported adopted
database does not insert another baseline record. Unknown structures are refused;
do not manufacture a history row or recreate the file to bypass the refusal.

Inspect the exit code after each operation (`$LASTEXITCODE` in PowerShell).
Do not continue to the next step after an error.

## Verify before restarting

```powershell
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db status --database src/Pulse.Api/pulse.db
dotnet run --project src/Pulse.Api
```

Require `current` with no pending migrations. Startup independently verifies
history and structure. Check `/health`, authenticate, and inspect a known project
and its representative data. Run a small capture in a designated test project
and confirm processing before considering the deployment validated.

## Handle an interrupted operation

Keep writers stopped. Record the command error and inspect the same path again.
Adoption records history in one write transaction; an uncommitted transaction
should roll back. Upgrades use EF's migration runner and provider locking.
Inspect the actual state instead of assuming that either the entire command
ran or none of it ran.

SQLite migration locks can survive an abruptly killed migration process. Do not
blindly remove a lock: first prove no migration process is still using the file,
preserve recovery evidence, and investigate the failed migration. Review the
official [SQLite migration-lock guidance](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations#concurrent-migrations-protection)
for the provider mechanism.

If recovery requires restoring the backup, restore to a separate path, verify it,
and point the matching application version at that restored file. Any writes made
after the backup are absent from that copy. Application rollback, feature
disablement, and backup restoration are different operations with different
effects; this workflow makes no promise of lossless rollback after new writes.

## Add the next migration as a developer

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add MeaningfulChange --project src/Pulse.Infrastructure --startup-project src/Pulse.Api --output-dir Migrations
dotnet test --filter FullyQualifiedName~DatabaseUpgradeTests
```

The local tool is pinned to the repository's EF package version. If your NuGet
configuration has no sources, explicitly restore from an approved source, such
as `dotnet tool restore --add-source https://api.nuget.org/v3/index.json`.
The design-time factory creates a temporary in-memory context and does not start
workers or select your regular database. Review generated operations and their
data effects. Keep `tests/Pulse.Tests/Fixtures/legacy-schema.sql` frozen, and add
preservation tests for the transition from the previous migration version.
