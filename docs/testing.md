# Testing

`tests/Pulse.Tests` holds all 291 tests (210 `[Fact]`s plus 12 `[Theory]`s
expanding to 81 cases — countable by text search, see ADR 0005) — fast unit tests over the pure
domain layer plus full-stack integration tests over the HTTP surface. The
whole suite runs in well under a minute.

```bash
dotnet test                          # everything
dotnet test --filter "FullyQualifiedName~QueryEdgeCaseTests"   # one class
```

## Taxonomy

| Folder | Style | What it covers |
| --- | --- | --- |
| `Domain/` | Pure unit tests, no host | Property-filter evaluation, cohort rule parsing, flag hashing (incl. rollout boundaries and a golden-value pin of the hash recipe), person-property merge rules, time bucketing, API key generation |
| `Infrastructure/` | EF-level tests | The `DateTimeOffset` → UTC-ticks converter (SQL ordering/range semantics), demo seeder |
| `Api/` | `WebApplicationFactory` integration tests | Everything else, exercised through real HTTP: auth, projects, capture + ingestion pipeline, identity merging, queries and their edge cases, cohorts, flags, dashboards, exports, the authz matrix, boundaries, hardening and production readiness |

Integration tests are the default here: most behavior worth asserting (authz,
validation, the async pipeline, query semantics) lives in the composition of
endpoint + service + database, not in any single class.

## The test host

`PulseApiFactory` boots the real app against a **private shared-cache
in-memory SQLite database**:

- The connection string is `Data Source=pulse-tests-{guid};Mode=Memory;Cache=Shared`
  — a *named* in-memory database. Every `DbContext` opens its own connection
  to the same name, so request handlers and the background workers (which run
  for real inside the test host) can operate concurrently; a single shared
  `SqliteConnection` would not be thread-safe.
- The factory holds one **keep-alive connection** open for its lifetime —
  a shared-cache in-memory database is dropped when its last connection
  closes.
- Each test class takes the factory via `IClassFixture<PulseApiFactory>`, so
  each class gets a fresh database and classes can run in parallel without
  seeing each other's data. Tests within a class run sequentially and share
  the database — create a fresh project per test for isolation.

See [ADR 0005](adr/0005-named-in-memory-sqlite-test-harness.md) for why this
beats both the EF in-memory provider and per-test files on disk.

## Testing the async pipeline: the drain helper

Capture returns 202 *before* events are persisted, so any test that captures
and then queries must wait for the ingestion worker:

```csharp
await client.PostAsJsonAsync("/capture", …);      // 202, queued
await TestIngestion.WaitForDrainAsync(client);     // polls /api/ingestion/metrics
// safe to query now
```

`WaitForDrainAsync` polls the public metrics endpoint every 20 ms until
`pending == 0` (default timeout 15 s, overridable for big batches). Because
it watches the real queue rather than sleeping a fixed interval, tests stay
fast when the worker is quick and correct when it is not. Dead-lettered rows
also leave the queue, so the helper works for poison-event tests too. A
transiently failing row is retried on the next processing pass — immediately
if other rows in its batch succeeded, otherwise on the next signal or the
worker's 1-second sweep — and dead-letters after its third attempt, at which
point the queue can reach zero.

Two other helpers:

- `TestAuth.RegisterAsync` / `AuthenticateAsync` — registers a throwaway user
  and installs its JWT on the client.
- `IngestionSignal.Ring()` (resolved from `factory.Services`) — wakes the
  worker immediately when a test inserts queue rows directly instead of going
  through `/capture`.

## Conventions

- One test class per feature area, `IClassFixture<PulseApiFactory>`, private
  helper methods at the bottom (`CreateProjectAsync`, `CaptureBatchAsync`,
  `Ev(...)` builders).
- Tests create their own project (and users) — never rely on data from
  another test.
- Fixed, explicit timestamps (`2026-03-01T10:00:00Z`) for query tests so
  bucket assertions are exact; `DateTimeOffset.UtcNow`-relative timestamps
  only where the behavior under test is time-relative (e.g. dynamic cohort
  lookback windows).
- Assert through the public API wherever possible; drop to a
  `factory.Services.CreateScope()` + `PulseDbContext` only to verify storage
  effects that no endpoint exposes (e.g. exact event rows after a purge).

## Flakiness policy

The suite runs real background workers, so determinism is a design
requirement, not an aspiration:

- **No bare sleeps.** Waiting is condition-based: `WaitForDrainAsync`
  polls every 20 ms (`TestIngestion.cs:25`) and `ExportTests.WaitForJobAsync`
  polls job status every 25 ms (`ExportTests.cs:270-285`). The only fixed
  sleep, `Task.Delay(1.5 s)` at `BoundaryTests.cs:230`, waits out a
  rate-limit window the test configured to 1 second (lines 210-211).
- **Every queue interaction ends drained**, so no test leaks pending work
  into the next test in its class.
- **Order-dependent behavior must be pinned, not assumed.** When a test
  exposed that funnel tie-breaking depended on SQLite's index scan order, the
  fix went into the product (deterministic tie-break by step index), not into
  a looser assertion.
- **Metrics assertions use deltas** (`before` → `after`) rather than absolute
  values, since counters are process-lifetime and shared within a class.
- The gate for merging test changes is the full suite passing **twice in a
  row**; a test that fails intermittently is treated as a bug (in the test or
  the product) and fixed, never retried into green.
