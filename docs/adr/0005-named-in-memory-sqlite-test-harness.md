# ADR 0005: Named shared-cache in-memory SQLite for integration tests

**Status:** accepted

## Context

Integration tests boot the real app with `WebApplicationFactory`, including
the hosted ingestion and export workers. That rules out the easy options:

- **EF InMemory provider** — not relational: no transactions, no
  `ExecuteUpdate`/`ExecuteDelete` (which the merge and purge paths use), and
  it would silently skip the UTC-ticks converter whose SQL behavior we
  explicitly want under test.
- **`Data Source=:memory:`** — one connection *is* the database. The
  background workers open their own `DbContext` (their own connection) and
  would see a different, empty database; sharing a single `SqliteConnection`
  across the host isn't thread-safe.
- **SQLite files on disk** — works, but needs temp-file lifecycle management
  and is an order of magnitude slower.

## Decision

`PulseApiFactory` points the app at
`Data Source=pulse-tests-{guid};Mode=Memory;Cache=Shared`:

- **Named + shared cache**: every connection using the same name reaches the
  same in-memory database, so request handlers and background workers each
  open their own connection and operate concurrently — exactly like
  production, minus the disk.
- **Keep-alive connection**: the factory opens one connection in its
  constructor and holds it until disposal, because a shared-cache in-memory
  database is destroyed when its last connection closes.
- **Guid per factory**: each test class (`IClassFixture<PulseApiFactory>`)
  gets a fresh, isolated database; xUnit can run classes in parallel with
  zero cross-talk.

Because capture is asynchronous, the harness pairs this with
`TestIngestion.WaitForDrainAsync`, which polls `/api/ingestion/metrics` until
`pending == 0` instead of sleeping.

## Consequences

- Tests exercise the real SQL surface — transactions, bulk updates, the
  ticks converter, index-driven ordering — at in-memory speed (the full
  291-test suite runs in ~30 s including the workers).
- Real concurrency in tests surfaces real bugs; the funnel tie-break issue
  (index scan order) was only observable because queries ran against actual
  SQLite.
- Within a class, tests share a database and must isolate by creating their
  own project/users — a convention, not an enforcement.
- SQLite is not the production engine for a system like this at scale;
  anything Postgres-specific would need a different harness. For this
  codebase SQLite *is* the shipped engine, so fidelity is total.
