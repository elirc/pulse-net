# Architecture decision records

Short records of the decisions that shaped pulse-net, in the order they were
made. Each captures the context at the time, the decision, and what it costs.

| ADR | Decision |
| --- | --- |
| [0001](0001-async-ingestion-durable-queue.md) | Capture returns 202 and persists through a durable queue, not synchronously |
| [0002](0002-404-not-403-membership.md) | Non-members get 404, never 403 |
| [0003](0003-sha256-deterministic-rollout.md) | Feature-flag rollout is a deterministic SHA-256 hash, not stored assignments |
| [0004](0004-three-api-key-types.md) | Three key types: write, read, personal |
| [0005](0005-named-in-memory-sqlite-test-harness.md) | Tests run on named shared-cache in-memory SQLite with a keep-alive connection |
| [0006](0006-utc-ticks-datetimeoffset-converter.md) | `DateTimeOffset` is stored as UTC ticks |
| [0007](0007-cursor-pagination-for-exports.md) | Exports paginate with `(timestamp, id)` cursors, not limit/offset |
| [0008](0008-sql-slice-in-memory-query-engine.md) | Queries filter in SQL, evaluate in memory |
