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
- A crash after 202 loses nothing (the queue is in the same SQLite database),
  and enqueue order is preserved across batches — the `$identify` ordering
  guarantee survives the async hop.
- Reads are eventually consistent: a query issued immediately after a 202 may
  not see the event. Tests (and careful clients) watch
  `GET /api/ingestion/metrics` until `pending` reaches 0.
- One poison event cannot wedge the queue (dead-letter path) or corrupt its
  neighbors (the processor clears the EF change tracker after a failed row).
- The single-worker design serializes ingestion; throughput is bounded by one
  consumer. Acceptable here; the queue table would also support competing
  consumers with row claiming if it ever mattered.
