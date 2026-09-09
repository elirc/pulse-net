# Capstone review: follow one event when several things go wrong

This lab connects features you studied separately. You will follow one small
project through admission, worker recovery, export, erasure, a bounded query,
and an hourly alert. The behavior already exists; this is practice reading,
predicting, reproducing, and explaining it.

Use a disposable database or the private test fixtures. Do not run erasure on
the only copy of data you care about. Before opening each answer, write the rows
you expect to exist and the user-visible result you expect to receive.

## The starting data

Project `P` has one visitor known first as `device-a`, then as `account-a` after
an `$identify` event. An hourly alert enabled before 09:00 watches `purchase`
with threshold 1. An
export will later copy event rows into its own snapshot. Times below are UTC.

```text
09:55  SDK sends pageview for device-a
10:00  SDK sends purchase for account-a with client event ID E1
10:02  worker tries to process the purchase
10:10  an event export is requested
10:20  an Admin requests erasure of the person
11:05  the completed 10:00–11:00 alert window is evaluated
```

Keep four records conceptually separate: the client UUID identifies the logical
client event across retries, a receipt reports processing outcomes, an export snapshot
owns copied input, and an alert evaluation records what was observed when it ran.

## Checkpoint 1: accepted response is lost

At 10:00, capture durably accepts the purchase under client event ID `E1` and
creates a requested receipt. The network drops before the SDK receives `202`.
It repeats the same payload with `E1`.

Predict:

1. Does the retry create a second queue row?
2. Does it receive a new processing identity or refer to the first one?
3. What changes if the same UUID carries a different payload?

<details>
<summary>Answer and code trail</summary>

The identical retry is deduplicated and refers to the original admission, so
the worker still has one logical item to process. Reusing `E1` with different
canonical content returns a conflict. The deduplication promise lasts seven
days; after expiry, `E1` may represent a new admission.

Follow [QueueAdmissionService](../../src/Pulse.Infrastructure/Services/QueueAdmissionService.cs)
from its fingerprint checks through the admission-key transaction, then read
[CaptureIdempotencyTests](../../tests/Pulse.Tests/Infrastructure/CaptureIdempotencyTests.cs).
Review [Lesson 16](16-admission-identity-and-receipts.md).

</details>

## Checkpoint 2: the worker loses its attempt

At 10:02, worker A owns project `P` with lease generation 4. It starts the
transaction that resolves the person, writes the event, updates the receipt,
and removes the queue row. Consider two crashes:

- Crash A happens before the transaction commits.
- Crash B happens immediately after the transaction commits, before the worker
  records any success in memory.

Predict the database state after restart. Then predict whether an expired worker
can commit after worker B acquires generation 5.

<details>
<summary>Answer and code trail</summary>

Crash A leaves the event, receipt transition, and queue acknowledgement
uncommitted, so the durable queue row remains available for another attempt.
Crash B leaves all three committed; restart sees no queue row and does not create
a second event merely because the old process missed its in-memory acknowledgement.

Every mutation transaction fences the lease generation. A stale generation
cannot commit after ownership changes. A transaction that already fenced and
holds SQLite write ownership finishes before a replacement claim can take over.
Delayed retry rows are not strict FIFO barriers: later due work can overtake a
row waiting for its retry time.

Trace [CaptureService](../../src/Pulse.Infrastructure/Services/CaptureService.cs),
[IngestionLeaseService](../../src/Pulse.Infrastructure/Services/IngestionLeaseService.cs),
and [IngestionProcessor](../../src/Pulse.Infrastructure/Services/IngestionProcessor.cs).
Reproduce the boundaries in
[IngestionTransactionTests](../../tests/Pulse.Tests/Infrastructure/IngestionTransactionTests.cs)
and [IngestionLeaseCommitTests](../../tests/Pulse.Tests/Infrastructure/IngestionLeaseCommitTests.cs).
Review [Lesson 17](17-retries-and-worker-ownership.md).

</details>

## Checkpoint 3: export copy meets erasure

At 10:10, an export freezes input that includes the purchase. At 10:20, erasure
freezes the person's known aliases, installs keyed suppression fingerprints,
pauses project data operations, and creates a cleanup job.

Predict what happens to the completed or in-progress export, its snapshot rows,
a stale export worker holding rendered content, and a delayed SDK event using
`device-a`. Would an entirely unknown alias be blocked?

<details>
<summary>Answer and code trail</summary>

Erasure invalidates every export in project `P`, clears downloadable content,
and deletes copied snapshot input. A stale export worker cannot publish its
rendered values because ownership and maintenance fences changed. The delayed
known alias is rejected even after cleanup completes; identify checks include
both the submitted distinct ID and the anonymous alias field.

Only aliases known when erasure began can be suppressed. The system cannot infer
that a never-seen future alias belongs to the erased person. External downloads,
backups, and third-party copies are outside this workflow.

Read [PersonErasureService](../../src/Pulse.Infrastructure/Services/PersonErasureService.cs),
[IdentitySuppressionService](../../src/Pulse.Infrastructure/Services/IdentitySuppressionService.cs),
and [ExportOwnershipService](../../src/Pulse.Infrastructure/Services/ExportOwnershipService.cs).
Use [PersonErasureWorkflowTests](../../tests/Pulse.Tests/Infrastructure/PersonErasureWorkflowTests.cs)
and [Lesson 21](21-person-erasure.md).

</details>

## Checkpoint 4: two kinds of analytical evidence

Suppose erasure has not happened yet. A bounded trend scans the 10:00–11:00
range, while the hourly alert evaluates that completed window at 11:05. A late
purchase arrives at 11:10 with a timestamp of 10:30.

Predict whether the bounded trend may include it on a later request, whether the
already stored alert evaluation changes, and whether either result describes the
same kind of snapshot as the export.

<details>
<summary>Answer and code trail</summary>

A later bounded query reads current stored events and may include the late row,
provided all work budgets pass. If a row, byte, bucket, annotation, or deadline
budget is exceeded, the endpoint returns an error rather than a partial success.

The alert evaluation stores the count observed at its actual evaluation time.
Version one does not reevaluate a completed window for late events, so its
evidence remains truthful but is not a permanent ingestion snapshot. The export
is different again: it renders from fixed copied input owned by that export job.

There is also a boundary difference: the bounded trend includes its `to` instant,
while alerts use `[start,end)`. A row exactly at 11:00 belongs to the trend range
shown here but to the next alert hour. Matching labels alone do not establish
matching query contracts.

Follow [BoundedQueryService](../../src/Pulse.Infrastructure/Services/BoundedQueryService.cs)
and [HourlyAlertService](../../src/Pulse.Infrastructure/Services/HourlyAlertService.cs).
Compare [BoundedAnalyticsLimitTests](../../tests/Pulse.Tests/Infrastructure/BoundedAnalyticsLimitTests.cs)
with [HourlyAlertTests](../../tests/Pulse.Tests/Infrastructure/HourlyAlertTests.cs).
Review [Lesson 23](23-bounded-trends.md) and [Lesson 25](25-hourly-alerts.md).

</details>

## Your review note

Without reopening the answers, explain why UUID deduplication, a lease fence,
an export snapshot, identity suppression, a bounded query result, and an alert
evaluation cannot replace one another. Then change one condition—reuse `E1`
after eight days, erase before export publication, or evaluate the alert after
the late event—and predict the new durable state before checking the code.

Record your work as prediction → reproduction → observation → explanation. A
test passing tells you what happened under its fixture; your explanation should
name the boundary that keeps the promise true after a crash, retry, or restart.
