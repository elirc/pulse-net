# One event's journey

**Our recurring example:** Mara's shop sends a `pageview` for visitor
`device-7`, with the property `page: /pricing`. Assume a valid project write
key and no failures for this first journey.

## 1. The shop sends a request

The request says `POST /capture`. Its JSON body names an action and an
identifier. Think: "Something happened; please record it."

Open [CaptureContracts](../src/Pulse.Api/Contracts/CaptureContracts.cs).
`CaptureRequest` is the C# shape the endpoint receives from the JSON.

## 2. The endpoint checks it

[CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs) resolves
the project using its write key, unwraps single/batch input, and checks
required event and distinct-ID fields. Bad input can stop here.

Think: "Do I know which project this belongs to, and is the basic data usable?"

## 3. It records pending work

The endpoint adds a `QueuedEvent` and saves it. It rings the signal, then
returns `202` with `{"status":"queued","queued":1}`.

Think: "The database now has a work item." Do not translate this into
"The analytics answer is ready." Processing has its own path.

## 4. The worker finds the work

[IngestionWorker](../src/Pulse.Api/Ingestion/IngestionWorker.cs) wakes from a
signal or periodic sweep. It creates a service scope and calls the processor.
[IngestionProcessor](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs)
reads queue rows in sequence order and validates the stored envelope.

Think: "What pending work can I attempt now?"

## 5. The service stores the event

[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs)
asks `IdentityService` which person owns `device-7`. If none exists, one is
created. It saves the event and updates event/property definitions.

The event writes and queue removal share a transaction. If acknowledgement
fails, those database changes roll back together.

Think: "Either the event and its related changes are committed and this
queue row is gone, or the transaction did not complete."

## 6. Mara asks for a count

The trend endpoint calls `QueryService`, which reads processed events and
groups matching activity into time buckets. In a fresh project, querying a
range containing this one event should count one `pageview` and one person.

## Now tell the same story in six verbs

**Send -> check -> queue -> process -> commit -> query.**

**Check:** when can failure happen after `202`? Where would an exhausted or
permanent processing failure be recorded?

<details>
<summary>Answer</summary>

The background processor can fail after acceptance. Permanent failures and
failures that exhaust retries are recorded in `DeadLetterEvents`. A query
does not treat those failed rows as successfully processed events.

</details>

Next: [watch the data change shape](05-data-shapes.md).
