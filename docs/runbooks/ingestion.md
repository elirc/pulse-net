# Ingestion investigation and recovery

Use this for local learning and as a starting point for an operational
procedure. This version uses fenced per-project worker leases. Stop every old
unfenced worker before upgrading the database and deploying it. Global metrics
remain public process diagnostics. See the [bootcamp verification record](../../astradocs/bootcamp/verification.md)
for the current implementation's tested scope.

## Symptom: accepted events are not queryable

1. Confirm the affected project, event name, timestamps, and query filters.
   A wrong range can look like missing ingestion.
2. Request `/api/projects/{projectId}/ingestion/metrics` with a member token.
   Record pending count, oldest age, and dead-letter count with the time.
   Also record delayed count and the next scheduled retry time. Delayed rows
   still count as pending; their age uses original enqueue time.
3. Inspect `/api/projects/{projectId}/ingestion/dead-letters?limit=20`.
   Payloads can contain user properties; keep them within authorized diagnostics.
4. Inspect local worker logs for database failures, validation errors, or
   shutdown. A single measurement does not distinguish backlog growth from
   recovery; compare observations over time.

| Observation | Investigate | Verify after action |
| --- | --- | --- |
| Pending and oldest age grow | Worker running? Storage available? Arrival rate above processing rate? | Age/depth fall and known event becomes visible |
| Pending zero, dead letters grow | Envelope/properties errors or exhausted retries | Root cause repaired; one replay succeeds |
| Pending zero, no matching events | Wrong project/range/filter, or previously failed work | Known event timestamp and identity match query |
| Health endpoint fails | Database reachability and application logs | Storage probes and a known event succeed |

## Replay one recoverable letter

Repair the underlying failure first. Inspect the payload and consider
whether an old identity/property update is still appropriate. Replay uses
the original bytes and a fresh retry budget, at the end of the queue.

In PowerShell, provide an existing project-admin token and IDs:

```powershell
$base = 'http://localhost:5141'
$projectId = '<project ID>'
$letterId = '<dead-letter ID>'
$memberToken = '<JWT or personal API key>'
$headers = @{ Authorization = "Bearer $memberToken" }
Invoke-RestMethod -Method Post -Headers $headers -Uri "$base/api/projects/$projectId/ingestion/dead-letters/$letterId/replay"
Invoke-RestMethod -Headers $headers -Uri "$base/api/projects/$projectId/ingestion/metrics"
```

`202` means queued. Wait for pending work to finish and check for new dead
letters plus the expected event or analytics result. If the event fails
again, investigate the new failure; repeated replay is not a repair.

`429` with `queue_capacity_exceeded` means the configured pending limit has no
room. The letter remains intact. `Retry-After: 1` suggests a delay, not a slot
reservation. `409` with `project_processing_paused` also retains the letter.

`422` means stored data is unsuitable for replay and the original letter
remains. The API does not edit malformed payloads. `404` can mean a missing,
already consumed, or inaccessible resource. Do not infer another project's
existence from a denied response.

## Guarantees and limits

Replay's remove-and-enqueue move is atomic. A storage failure rolls it back.
The original letter is consumed on success. New processing items retain their
admission identity and latest 20 transition records; replay increments their
generation. Legacy letters without processing metadata do not gain invented
historical states. The queue timestamp is new; an explicit event timestamp remains
unchanged, while a null event timestamp is assigned during processing.

Project counts are database observations; separate queries can observe
slightly different instants. Global totals are process-lifetime diagnostics,
not durable accounting. Restart resets them. A signal is optional for
correctness because the worker periodically sweeps the durable queue.

After an exercise, write an [incident note](../learning/templates.md) with
the symptom, evidence, recovery, verification, and a prevention improvement.

## Diagnose before replaying a batch

Use `GET P/ingestion/dead-letters/ID/replay-check` to inspect stable validation
issues without consuming the letter. `POST P/ingestion/dead-letters/replay-batch`
accepts 1–20 unique IDs and runs one transaction per item. Expected results
include queued, invalidPayload, notFound, capacityExceeded, and paused. An
unexpected storage error stops later work; earlier committed items remain queued.
Keep that partial-progress boundary in the incident note.

`GET P/ingestion/status` provides ordered reason codes. Investigate worker/storage
state for `oldest_pending_too_old`, compare arrival and drain rates for
`queue_depth_high`, and inspect failed work for `dead_letters_present`. These
are observations, not an automatic repair plan. Threshold equality counts as
attention. `POST P/capture/validate` checks a candidate envelope without writing
queue rows or asking for a capture credential.

## Client retry and receipt guidance

Generate one UUID event_id for each logical SDK event and retain it across
network retries. Inside seven days, equivalent normalized retries return the
original admission; conflicting payloads under the same live ID return 409 for
the whole batch. Retries never extend the window. Omitted IDs keep ordinary
capture behavior. Number spelling matters in fingerprint version 1: 1 and 1.0
conflict, while reordered object members do not.

`X-Capture-Receipt: true` returns a status URL. Inspect it with a project member
credential; write-key possession alone is insufficient. Counts are by submitted
position, so duplicate positions can share one processing item. Processed means
the event and acknowledgement committed together. It does not promise perpetual
event retention. Queued receipts have no fixed expiry; terminal receipts expire
seven days after their final terminal transition, with replay reopening them.

## Capacity, retries, and ownership

Admins set `PUT P/ingestion/limits` with `maxPending` null (disabled) or 1–100000.
Capture, replay, and edits use the same durable gate. Lowering the limit retains
existing work. Only newly admitted rows consume slots; fully deduplicated retries
can succeed while depth is at or above a lowered limit.

Recognized SQLite BUSY/LOCKED failures schedule 1, 2, 4, then 8 seconds before
subsequent attempts. The fifth failed attempt dead-letters with count five.
Malformed envelopes dead-letter immediately with one failed attempt. Cancellation
and unclassified storage failures consume no attempt; investigate the logged
operational failure instead of repeatedly replaying the payload.

Leases last 30 seconds and include an owner ID and increasing generation.
Different projects may have different owners; each mutation checks ownership
inside its transaction. Expired owners are reclaimable, and a resumed stale
owner cannot use its older token. Inspect lease owner/generation/expiry and
pending due times when diagnosing stalled work. Do not delete leases to let a
still-running unfenced worker continue writing.

Supported multi-instance topology uses the same supported local SQLite storage
environment with sufficiently aligned clocks. SQLite write contention remains;
a second worker is not a promise of linear throughput. Capture, processing,
replay, and cleanup must all run the matching schema-aware version after upgrade.
