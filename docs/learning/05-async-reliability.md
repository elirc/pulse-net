# Async processing, retries, and recovery

**Outcome:** reason about delivery, ordering, and shutdown without assuming
that a background worker always completes its current method.

## The channel is a bell

`IngestionSignal` holds a bounded channel of booleans. Payloads live in the
database. A full channel can drop a notification because the worker sweeps
periodically. Losing the bell does not discard the event.

Compare an in-memory-only queue: process exit would lose whatever had not
been persisted. Draw both lifecycles and mark where each can return `202`
while honoring its stated durability promise.

## Retries change ordering

The processor reads in sequence order, but after a transient failure it
continues through the batch. A later event can succeed before an earlier
one retries. For `$set` updates, that can change the final property value.
The happy path's ordering tests do not prove strict ordering under failure.

A senior design question is whether to stop all processing, pause one
identity's partition, or accept this behavior. Each has a cost: availability,
complexity, or weaker ordering. Specify the required behavior first.

## Classify failures by evidence

| Failure | Current behavior | Question to ask |
| --- | --- | --- |
| Unreadable envelope or missing identity/name | Immediate dead letter | Can the same bytes ever work? |
| Corrupt nested properties JSON | Consumes retry budget | Should deterministic parse failures be classified earlier? |
| Ingest storage exception | Retry, then dead letter after budget | Is the dependency recovering; how long should we wait? |
| Cancellation | Propagates to worker shutdown handling | Did uncommitted writes roll back? |
| Process exit after replay commit, before bell | Queue survives; periodic sweep finds it | Where is the durable source of truth? |

`Attempts` currently records retries before the final failed try; after the
third failure it is two. This is documented behavior, not a new count of
all attempts. Changing it requires updating the contract and tests together.

## Recovery is a feature with its own failure paths

Replay consumes one dead letter and creates one fresh queue row in the same
transaction. It resets retries and retains the stored payload bytes. The
validation check rejects obviously broken payloads; it cannot guarantee the
pipeline will succeed after a dependency or logic failure.

Replay of an old `$identify` or `$set` event can affect current identity and
properties. Fix the cause and inspect one event before replaying more.
There is no bulk replay feature or permanent replay audit log in this change.

## Exercise

Write a test with an earlier failing update and a later valid update for the
same person. Describe the final-state policy you want before implementing
it. Use controlled failure injection rather than a random sleep.

**Stretch:** design exponential backoff with a next-attempt timestamp and an
injected clock. Explain how it interacts with ordering and starvation.

**Done when:** you can discuss at-least-once delivery, client idempotency,
queue acknowledgement, failure isolation, and recovery as distinct concerns.
