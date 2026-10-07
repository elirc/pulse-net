# ADR 0001: Capture returns 202 through a durable queue, not synchronously

**Status:** accepted

## Context

`POST /capture` is the hottest endpoint and the one clients are least able to
retry intelligently — SDKs fire-and-forget from user devices. v1 ingested
synchronously: person resolution, `$identify` merges and definition upserts
all ran inside the request, so capture latency scaled with pipeline cost, and
a transient failure surfaced as an error the SDK could only handle by
re-sending (risking duplicates) or dropping data.

Ordering also matters: `$identify` must observe the anonymous events enqueued
before it, and `$set` writes to the same person must apply in arrival order.

## Decision

Capture validates the payload shape, appends each event to a `QueuedEvents`
table (auto-increment `Seq` = arrival order) in one `SaveChanges`, and
returns **202 `{ status: "queued" }`**. A hosted `IngestionWorker` drains the
table in `Seq` order in batches of 200, pushing rows through the same
`CaptureService` the sync path used.

The wake-up is a bounded `Channel<bool>` of capacity 1 (`DropWrite`) used
purely as a doorbell — the table is the source of truth, so a missed signal
only delays work until the worker's 1-second periodic sweep.

Failures are classified: rows that can never succeed (unparseable payload,
failed re-validation, vanished project) dead-letter immediately into
`DeadLetterEvents`; anything else retries up to 3 attempts before
dead-lettering with the error. Dead letters are inspectable per project.

## Consequences

- Capture latency is one queue insert regardless of pipeline cost; bursts
  absorb into the queue instead of into request latency.
- A crash after 202 loses nothing (the queue is in the same SQLite database).
  Rows are *attempted* in `Seq` order across batches, so in the normal path
  the `$identify` ordering guarantee survives the async hop. Two caveats are
  visible in the code (see "Review notes" below): a row that fails
  transiently is retried *after* later rows in its batch have been
  processed, and processing is at-least-once rather than exactly-once.
- Reads are eventually consistent: a query issued immediately after a 202 may
  not see the event. Tests (and careful clients) watch
  `GET /api/ingestion/metrics` until `pending` reaches 0.
- One poison event cannot wedge the queue (dead-letter path) or corrupt its
  neighbors (the processor clears the EF change tracker after a failed row).
- The single-worker design serializes ingestion; throughput is bounded by one
  consumer. Acceptable here; the queue table would also support competing
  consumers with row claiming if it ever mattered.

## In the code

| Piece | Location |
| --- | --- |
| Enqueue one `QueuedEvent` per event, one `SaveChangesAsync`, ring, 202 | `src/Pulse.Api/Endpoints/CaptureEndpoints.cs:48-62` |
| Doorbell: bounded channel, capacity 1, `DropWrite` | `src/Pulse.Infrastructure/Services/IngestionPipeline.cs:14-34` |
| Worker loop: wait for signal or 1 s sweep, then drain until a pass moves nothing | `src/Pulse.Api/Ingestion/IngestionWorker.cs:12`, `28-60` |
| `MaxAttempts = 3`, `BatchSize = 200`, `OrderBy(q => q.Seq).Take(BatchSize)` | `IngestionPipeline.cs:59-60`, `76-80` |
| Per-row outcome switch: delete / dead-letter + delete / bump `Attempts` | `IngestionPipeline.cs:94-120` |
| Permanent failures (missing project, unparseable or invalid payload) | `IngestionPipeline.cs:143-163` |
| Transient failure: `ChangeTracker.Clear()`, then retry or dead-letter | `IngestionPipeline.cs:170-178` |
| Public queue metrics; member-only dead-letter listing | `src/Pulse.Api/Endpoints/IngestionEndpoints.cs:15-28`, `31-45` |

## Review notes

- **Retry reorders.** The `Retry` branch (`IngestionPipeline.cs:115-119`)
  only increments `Attempts`, and the `foreach` continues, so later rows in
  the same batch are ingested before the failed row's next attempt. If the
  failed row was an `$identify`, events after it are processed against the
  pre-merge person. The next `ProcessPendingAsync` picks the failed row up
  again immediately if the pass moved anything; if the batch contained only
  retries, `DrainAsync` stops (`IngestionWorker.cs:56-58`) and the row waits
  for the next signal or 1 s sweep. There is no backoff between attempts.
- **At-least-once, not exactly-once.** A processed row is deleted in a
  separate statement after `CaptureService` has saved
  (`IngestionPipeline.cs:96-99`); dead letters likewise (`:111-112`). A
  crash between the two leaves the row queued and it is ingested again on
  restart — a duplicate event. One transaction around ingest + delete, or
  deduplication by event id, would close this.
- **Metrics are global and unauthenticated.** `/api/ingestion/metrics`
  counts queue and dead-letter rows across *all* projects with no auth
  (`IngestionEndpoints.cs:13-28`). Fine for a demo; in multi-tenant use it
  reveals aggregate activity.

**Check:** reading only `IngestionPipeline.cs:74-125`, give the order in
which three queued rows (Seq 1 fails transiently once, Seq 2 and 3 succeed)
are committed and how many `ProcessPendingAsync` calls that takes.
(Answer: 2, 3, then 1, over two calls.)
