# 0009: Commit ingestion effects and acknowledgement together

Status: accepted.

## Context

Capture previously committed the event transaction before the processor
deleted its queue row. A crash in between could cause another ingest of the
same queued event. Dead-letter insertion had an equivalent split-commit
window. Operators also needed a recovery path for failed events.

## Decision

CaptureService shares its persistence path between direct calls and queued
calls. For a queued call, the event, identity, definitions, and queue deletion
commit in one SQLite transaction. Dead-letter creation and queue deletion
likewise commit together.

An operations service atomically consumes one project-scoped dead letter
and inserts a fresh queued row after checking its envelope and properties.
The HTTP endpoint requires project membership. It returns 202 after commit,
404 for unavailable letters, and 422 for invalid stored payloads. It rings
the worker signal only after commit. Project metrics expose backlog count
and age using the injected clock.

## Alternatives

Keeping separate commits is simpler but admits partially completed durable
state. In-memory deduplication would not survive restart. A transactional
move in the existing database gives a bounded improvement without changing
the schema or introducing a second storage system.

## Consequences

Transactions include queue deletion and hold database locks slightly longer.
Database failure-injection tests establish rollback behavior. This closes
the split-commit window under the current single-worker architecture; it
does not promise deduplication of separate client requests or multi-worker
coordination.

Replay appends at the tail, resets retries, and consumes the original letter.
It does not retain a replay audit trail or restore original ordering. That
choice keeps recovery small and explicit; audit history is a follow-up exercise.

Metrics are operational readings, not durable per-project throughput totals
or one globally consistent snapshot. Revisit this design when introducing
multiple workers, event idempotency, audit requirements, or another database.
