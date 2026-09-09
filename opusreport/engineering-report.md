# pulse-net — Engineering State Report

**Audience:** software engineers joining or reviewing this codebase
**Date:** 2026-09-09
**Reviewed at:** `main` @ `c628672`, plus the uncommitted working tree
**Verified on this machine:** .NET SDK 10.0.400, Windows 11

---

## 1. Verified facts

Everything in this section was executed or measured during the review, not read
from documentation.

| Check | Command | Result |
| --- | --- | --- |
| Build | `dotnet build` | **Succeeded — 0 errors, 0 warnings** (46 s) |
| Tests | `dotnet test --no-build` | **656 passed, 0 failed, 0 skipped** (5 min 1 s) |
| Test methods | `[Fact]`/`[Theory]` count | 456 declarations → 656 executed cases |
| Source size | `.cs` under `src/` | 26,263 lines / 266 files (≈11,000 lines are EF migration scaffolding) |
| Test size | `.cs` under `tests/` | 13,094 lines / 100 files |
| Route patterns | `Map{Get,Post,Put,Delete}` | 110 distinct patterns (127 registrations) |
| EF entities | `DbSet<>` in `PulseDbContext` | 42 entity sets, 54 explicit indexes, 10 migrations |
| NuGet packages | `src/**/*.csproj` | **4 total** (JwtBearer, EFCore.Design, EFCore.Sqlite, SQLitePCLRaw) |
| Debt markers | `TODO`/`FIXME`/`HACK` in `src` + `tests` | **0** |
| Documentation | `*.md` excluding `tmp/` | 97 files, ~124,000 words |

Note the test suite duration recorded in
[verification.md](../astradocs/bootcamp/verification.md) (7 min 57 s / 8 min 2 s)
was measured under `DOTNET_PROCESSOR_COUNT=2` with stack sampling active. The
5-minute figure here is the unconstrained run. Both are integration-heavy: most
tests boot a real `WebApplicationFactory` host with background workers running
against shared-cache in-memory SQLite.

---

## 2. The single most important thing to know

**Roughly two thirds of the codebase has never been committed.**

```
tracked   .cs files: 102        untracked .cs files: 164
tracked   .md files: 14         untracked .md files: 83
working tree entries: 205 (56 modified + 149 untracked)
tracked-file diffstat: 56 files changed, +1,835 / −562
HEAD == origin/main == c628672   (nothing is staged, nothing is pushed)
```

`main` and `origin/main` both point at `c628672` ("Merge pull request #17"), the
end of the original 17-sprint arc. Every subsequent piece of work — the entire
75-story bootcamp implementation — exists only as uncommitted files on one
developer's disk:

- **All 10 EF Core migrations** and the schema-lifecycle machinery
  (`src/Pulse.Infrastructure/Migrations/`, `src/Pulse.Infrastructure/Schema/`)
- **~20 new endpoint modules** — retention, erasure, hourly alerts, flag
  governance, bounded analytics, dashboard templates, person activity,
  management audit, project discovery, export history
- **~20 new infrastructure services** — `BoundedQueryService`,
  `ExportSnapshotService`, `ExportOwnershipService`, `EventRetentionService`,
  `CaptureReceiptService`, `PersonErasureService`, `HourlyAlertService`, …
- **8 new domain entities** plus `QueryWorkBudget` and `SessionGrouping`
- **The whole authorization redesign** — `ProjectPermissionMatrix`,
  `RestrictedToken`, `AuthenticatedActor`, `ProjectMaintenanceAccess`
- **All of `astradocs/`** (54 files), `docs/learning/`, `docs/runbooks/`,
  ADRs 0009–0010, `docs/project-permissions.md`, and `scripts/`

There is no branch, no stash, no remote copy. A disk failure or an accidental
`git clean -fdx` destroys months of work. **Committing this is the highest-value
action available and should happen before anything else in this report.**

A secondary consequence: the verification evidence referenced throughout
`astradocs/bootcamp/verification.md` lives in `tmp/*.trx` and
`tmp/upgrade-rehearsal-*/evidence.json`, and `tmp/` is gitignored
(`.gitignore:483`). The evidence trail is not reproducible from a fresh clone.

---

## 3. Architecture

Three projects, strictly layered, dependencies pointing inward:

```
Pulse.Domain          entities + pure logic (filters, cohort rules, flag hashing,
                      time bucketing, CSV, password hashing) — zero dependencies
      ↑
Pulse.Infrastructure  PulseDbContext, ~30 services, ingestion/export processors,
                      migrations, schema lifecycle, demo seeder
      ↑
Pulse.Api             minimal-API endpoint modules, contracts, auth handlers,
                      6 hosted background workers, validation
      ↑
tests/Pulse.Tests     Domain (unit) / Infrastructure + Api (integration)
```

`Program.cs` is 247 lines and registers everything explicitly — no assembly
scanning, no convention magic. You can read the entire composition root in one
sitting, which is a genuine asset.

### Ingestion path

`POST /capture` → validate envelope → append to `QueuedEvents` (durable) →
**202** → `IngestionSignal` (a `Channel`) wakes `IngestionWorker` →
`IngestionProcessor` persists in enqueue order inside a shared transaction →
poison payloads move atomically to `DeadLetterEvents`.

The interesting parts are the ones added by the bootcamp work:

- **Admission identity.** Project-scoped client UUIDs deduplicate matching
  retries for 7 days (`CaptureAdmissionKey`, `CaptureFingerprint`).
- **Worker leases with fencing.** `ProjectIngestionLease` +
  `IngestionWorkerIdentity`; every mutation proves current owner *and*
  generation inside its own transaction, so a stale worker cannot publish.
- **Receipts.** `CaptureReceipt` / `CaptureReceiptItem` give clients a durable
  per-item outcome instead of relying on in-process counters.
- **Atomic replay.** Single-letter and batch dead-letter replay, each proven by
  rollback tests.

### Query engine

Deliberately split (ADR-0008): time range and event name filter **in SQL**
against the ticks-indexed timestamp column; property filtering, bucketing,
breakdowns, funnel traversal and cohort math run **in memory** on the resulting
slice. `QueryService` (418 lines) is the legacy unbounded path;
`BoundedQueryService` + `QueryWorkBudget` is the newer contract that *rejects*
excessive work rather than returning a partial aggregate. Both are live — the
legacy routes intentionally keep their original behavior.

### Time storage

Every `DateTimeOffset` is stored as UTC ticks (`long`) via a model-wide value
converter, because SQLite maps `DateTimeOffset` to TEXT by default and TEXT
cannot be ordered or range-compared correctly. Export cursors embed the same
ticks value so cursor comparison matches column comparison. This is documented
in ADR-0006 and is the correct call for the storage engine chosen.

### Auth

A policy scheme dispatches on the `Authorization` header prefix: `pk_user_`
goes to `PersonalApiKeyAuthenticationHandler` (SHA-256 hash lookup), everything
else to JWT bearer. Four credential types:

| Credential | Prefix | Storage | Grants |
| --- | --- | --- | --- |
| JWT session | — | not stored | current project role; required to manage personal keys |
| Personal API key | `pk_user_` | **SHA-256 hashed** | current project role; restricted keys add project/scope/expiry limits |
| Project write key | `pk_live_` | **plaintext** | `/capture`, `/decide` |
| Project read key | `rk_live_` | **plaintext** | allowlisted analytics routes via `X-Api-Key` |

Authorization decisions are centralized in `ProjectPermissionMatrix` and
`ProjectAccessService`. Non-members get **404** (ADR-0002 — don't leak project
existence); members with insufficient role get **403**. This is enforced by
`AuthzMatrixTests` (270 lines).

---

## 4. Code quality assessment

**Strong.** In no particular order:

- Zero build warnings across 26k lines with `<Nullable>enable</Nullable>` on
  every project.
- Zero TODO/FIXME/HACK markers.
- 4 third-party packages in the entire application. Supply-chain surface is
  about as small as a real .NET web app can get.
- Integration tests exercise the *real* pipeline including hosted workers, not
  mocks. `TestIngestion` provides a drain helper so async ingestion is testable
  without sleeps.
- Rollback behavior is tested explicitly — failed-delete, acknowledgement
  failure, erasure-initiation rollback, dashboard copy/import rollback.
- 10 ADRs actually explain trade-offs rather than restating what the code does.
- XML doc comments on the non-obvious types, and the comments explain *why*
  (e.g. the ticks converter, the policy scheme, the rate-limiter's no-queue
  choice).

**Rough edges:**

- `src/Pulse.Api/Auth/ProjectMaintenanceAccess.cs` is written in a dense
  multi-statement-per-line style (`var http = context.HttpContext; var rule = …;`)
  that does not match the rest of the codebase. It also holds one of the most
  consequential behaviors in the system (§5.1). Worth reformatting.
- `DashboardEndpoints.cs` (406 lines) and `ExportEndpoints.cs` (347) are the
  largest hand-written files and are approaching the point where splitting by
  resource would help — the pattern already exists elsewhere
  (`DashboardCompositionEndpoints`, `DashboardTemplateEndpoints`).
- Route registration mixes absolute paths (`/api/projects/{projectId:guid}/…`)
  with group-relative paths (`/{key}/clone`) across modules, so grepping for a
  route requires knowing which module owns the group.

---

## 5. Known limitations, ranked by consequence

The project's own [review-findings.md](../docs/learning/review-findings.md)
already documents most of these honestly. That is unusual and to its credit —
the list below adds code locations and my assessment of severity.

### 5.1 Protected reads serialize globally — the throughput ceiling

`ProjectReadSnapshotFilter` ([ProjectMaintenanceAccess.cs:38-59](../src/Pulse.Api/Auth/ProjectMaintenanceAccess.cs#L38-L59))
wraps every member-facing `GET`/`POST` read in `BeginTransaction(deferred: false)`,
which takes SQLite's **RESERVED** lock. That is a writer lock. The purpose is
correct — it enforces erasure-pause ordering so a read cannot straddle a
maintenance generation change — but the effect is that *all* protected reads
serialize with each other and with every writer in the process.

Practically: concurrent dashboard refreshes, trend queries and person lookups
execute one at a time regardless of core count. This is the binding constraint
on read concurrency and should be measured before any capacity claim is made.

### 5.2 Rate-limit identity does not match authentication identity

`Program.cs:114-127` partitions the `/capture` limiter on the `X-Api-Key`
header, falling back to client IP. But `/capture` also accepts the write key in
the request **body** (`api_key`), which the README documents as the primary
form. So body-key clients all share one IP partition, and a caller can present
an arbitrary `X-Api-Key` header value to land in an unused partition while
authenticating with a different body key. Fix: partition on the authenticated
project id resolved by the endpoint, not on a raw header.

### 5.3 Committed development secret

`src/Pulse.Api/appsettings.json` ships
`"Secret": "pulse-net-dev-signing-secret-0123456789abcdef-change-in-prod"`.
Startup throws if `Jwt:Secret` is absent, but it is never absent — the checked-in
default satisfies the guard. Anyone with the repo can forge session tokens
against any deployment that did not override it. Remove the default so the
existing `InvalidOperationException` actually fires.

### 5.4 Diagnostic exposure

`GET /api/ingestion/metrics` is unauthenticated by design ("health-style") and
reports global queue depth, dead-letter count and lifetime processed counters
across all projects. Separately, dead-letter rows retain full event payloads
indefinitely behind project authorization. Both need a deliberate retention and
exposure policy before public operation.

### 5.5 Per-identity ordering under retry

Eligible ingestion rows omit retries whose persisted due time is in the future,
so a later event for the same identity can overtake an earlier failed one.
Person-property `$set` semantics are order-dependent, so this can produce a
wrong final person state. No per-identity ordering policy exists yet.

### 5.6 Deduplication has a finite, optional lifetime

Client UUID deduplication expires after 7 days and clients are not required to
send one. A retry outside that window, or from a client that omits the key,
creates duplicate work silently.

### 5.7 Counters are in-process

`IngestionCounters` is a singleton updated after committed row outcomes. A crash
between commit and increment loses telemetry permanently. Use `CaptureReceipt`
rows for anything accounting-shaped; the counters are indicative only.

### 5.8 Legacy query materialization

`QueryService.LoadEventsAsync` materializes the filtered slice into memory. Wide
date ranges on a large project can allocate heavily. `BoundedQueryService` fixes
this for the new `/insights/trend-bounded` route, but the original `/insights/trend`,
`/insights/funnel` and `/insights/retention` routes retain the old behavior and
are the ones the dashboards use.

---

## 6. Infrastructure gaps

These are absences, not defects — they are simply not built:

| Missing | Impact |
| --- | --- |
| **No CI** (no `.github/`, no pipeline of any kind) | The 656-test suite runs only when someone remembers. Nothing gates a merge. |
| **No Dockerfile / deployment manifest** | No reproducible deployment artifact. |
| **No OpenAPI document** | `docs/api-reference.md` (757 lines) is hand-maintained for 110 routes. It is currently accurate but has no mechanism keeping it that way, and no client can be generated. |
| **No CORS policy** | Any browser-based consumer is blocked. |
| **No HTTPS redirection / HSTS** | Relies entirely on a terminating proxy that does not exist yet. |
| **No metrics export** (no OpenTelemetry, no Prometheus) | Observability is structured log lines only. `RequestLoggingMiddleware` emits trace/span ids but nothing collects them. |
| **No coverage reporting** | `coverlet.collector` is referenced but no report is produced or tracked. |
| **SQLite only** | Single-file store, single-process writer. See §5.1. |

---

## 7. Recommended sequence

**Immediately**

1. Commit and push the working tree. Split it into reviewable commits if you
   like, but get it off one disk today. This is not a code-quality question.
2. Decide whether `tmp/` evidence artifacts should be preserved somewhere
   tracked, since the verification record cites them by path.

**Before anyone else clones it**

3. Add a CI workflow: `dotnet build` + `dotnet test`. The suite already passes
   clean; wire it up before it stops doing so.
4. Fix `README.md:250` — it still claims the schema is created with
   `EnsureCreated()` on startup and tells you to delete `pulse.db` after pulling.
   Neither is true: `VerifyDatabaseInitializer` refuses to start on a database
   that isn't current, and upgrades happen through explicit offline
   `db status` / `db adopt-legacy` / `db upgrade` commands. The journal claims
   this was corrected; the README line survived. A new developer following it
   will delete their database for no reason and then be confused when startup
   throws.
5. Remove the JWT secret default from `appsettings.json` (§5.3).

**Before any non-toy deployment**

6. Fix the rate-limit partition key (§5.2) — small change, real exposure.
7. Measure §5.1. Either accept serialized reads with a documented concurrency
   ceiling, or move the erasure-ordering guarantee to a mechanism that doesn't
   require a writer lock on the read path.
8. Decide the exposure policy for global metrics and dead-letter payload
   retention (§5.4).

**When the dataset grows**

9. Migrate dashboards onto the bounded query contract, or backport the work
   budget into `QueryService`.
10. Revisit SQLite. The ticks converter, the cursor design and the lease/fencing
    model all port cleanly to Postgres; the read-lock filter in §5.1 is the piece
    that would be redesigned rather than ported.

---

## 8. Where to start reading

1. [`docs/architecture.md`](../docs/architecture.md) — 226 lines, accurate, current.
2. [`docs/adr/`](../docs/adr/README.md) — 10 ADRs, read 0001, 0006 and 0008 first.
3. [`src/Pulse.Api/Program.cs`](../src/Pulse.Api/Program.cs) — the whole composition root.
4. [`docs/learning/worked-example-ingestion.md`](../docs/learning/worked-example-ingestion.md) —
   one feature traced through authorization, transaction, failure test and runbook.
5. [`astradocs/bootcamp/capstone-review.md`](../astradocs/bootcamp/capstone-review.md) —
   connects retries, worker crashes, exports, erasure, bounded queries and alerts
   in a single timeline. The best single document for understanding how the
   reliability mechanisms interact.
