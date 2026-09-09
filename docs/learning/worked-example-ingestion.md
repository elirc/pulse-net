# Worked example: recover one failed event

Read this after trying to specify replay yourself. This is a walkthrough of
the implemented feature, not an exercise you still need to build.

## 1. Start with the operator's problem

An event exhausted retries during a temporary failure. The cause is fixed,
but the event is in dead-letter storage. A project member needs to inspect
the backlog and submit that event for another processing attempt.

Requirements: authorize the member; operate only on the requested project's
letter; retain invalid evidence; move valid data without loss; reset the
retry budget; report accepted work separately from completed processing.

## 2. Write the contract

```text
GET  /api/projects/{projectId}/ingestion/metrics
POST /api/projects/{projectId}/ingestion/dead-letters/{letterId}/replay
```

Metrics returns `pending`, `deadLetters`, `oldestEnqueuedAt`, and
`oldestPendingAgeSeconds`. An empty queue uses null for the two oldest fields.
Replay takes no body and returns `202 {"status":"queued","queued":1}`.
It does not accept edited event data, so operators cannot silently rewrite
the evidence through this endpoint.

## 3. Assign responsibilities

[IngestionEndpoints](../../src/Pulse.Api/Endpoints/IngestionEndpoints.cs)
performs membership checks and maps outcomes to HTTP.
[IngestionOperationsService](../../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs)
owns the queries and replay transaction. The enum describes service outcomes
without depending on HTTP result types.

The clock is injected because age and replay enqueue time depend on the
current instant. The project predicate appears in every operational query.
The transaction begins before the letter lookup and covers delete plus insert.

## 4. Walk every exit

| Exit | Durable result |
| --- | --- |
| Letter missing in this project | No changes; 404 |
| Envelope or properties rejected | Original letter retained; 422 |
| Enqueue fails after delete | Transaction rolls back; original letter retained |
| Commit succeeds | Letter gone; fresh queued row with attempts zero |
| Signal missed after commit | Worker periodic sweep discovers the row |
| Worker fails again | Normal retry/dead-letter policy applies |

Two replay requests cannot both consume the same letter successfully in the
tested SQLite setup. The concurrency test sends both and requires one `202`
and one `404`, then checks there is one stored event. This is not a substitute
for a worker leasing design across multiple application instances.

## 5. Test a failure at the dangerous point

[IngestionTransactionTests](../../tests/Pulse.Tests/Infrastructure/IngestionTransactionTests.cs)
installs a database trigger that rejects enqueue insertion. The delete has
already executed inside the transaction. The test establishes that the
letter still exists after the exception. This is stronger than checking the
happy path or checking that a transaction method was invoked.

The same test file probes acknowledgement rollback in the ingestion path.
That review led to a shared transaction for event writes and queue removal.

## 6. Explain the limits in the review

Replay appends at the tail and cannot restore original ordering. It preserves
the stored timestamp, including null; null is resolved by capture at processing
time. It consumes the original letter rather than retaining a permanent audit
record. Metrics are operational observations: the queue aggregation and
dead-letter count are separate reads, not a global consistent snapshot.

**Teach-back:** explain why signal-after-commit matters, why `202` is honest,
why project scoping belongs in the query, and why HTTP idempotency remains a
separate feature. Then design a replay history table without implementing it.
