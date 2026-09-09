# Session 17 — Retry later without letting an old worker write

This session follows SR-10 and SR-11. The code and tests are under integration;
the [verification record](verification.md) records actual execution evidence.

A request returning 202 is the beginning of processing. The worker must handle
temporary contention, malformed data, cancellation, and process replacement.
Do not give these different situations the same “catch everything and retry”
policy. First identify what the failure tells you.

## Classify the evidence

| Situation | What you know | Policy |
| --- | --- | --- |
| Envelope/properties are malformed | This stored work cannot be parsed as supported input | Dead-letter immediately |
| SQLite reports BUSY or LOCKED | Storage contention prevented this attempt | Persist a bounded delayed retry |
| Cancellation | The operation was asked to stop | Consume no attempt |
| Unrecognized storage/application exception | The cause is not classified as retryable or poison | Abort/log the cycle; preserve work for investigation |

Find the concrete error-code checks in
[IngestionRetryPolicy](../../src/Pulse.Infrastructure/Services/IngestionRetryPolicy.cs).
The words “busy” inside an arbitrary exception message do not establish a
SQLite contention category. An injected constraint violation is therefore an
operational failure, not evidence that the payload should burn a retry budget.

The repository's older tests treated malformed nested properties as transient
poison and counted previously completed retries. The new contract validates
the complete envelope and consistently records failed attempts. An immediate
permanent failure records one failed attempt; exhaustion records five.

## Persist the schedule, then return to other work

| Failed attempt | Next action |
| --- | --- |
| 1 | Eligible after 1 second |
| 2 | Eligible after 2 more seconds |
| 3 | Eligible after 4 more seconds |
| 4 | Eligible after 8 more seconds |
| 5 | Move to dead letters with five failed attempts |

The delay is a `NextAttemptAt` value on the queue row, not a sleeping thread
inside a transaction. A new processor reads that same value after restart.
One tick before it is too early; equality is eligible. `EnqueuedAt` remains
the original queue-age timestamp, so repeated failures do not make old work
look freshly admitted.

In [IngestionProcessor](../../src/Pulse.Infrastructure/Services/IngestionProcessor.cs),
find the rollback/clear step before persisting a retry. Failed capture can leave
tracked people, definitions, or events in the context. Those entities must not
leak into the next row's save. The retry update records attempt count, due time,
and a safe code together. If even that update fails, the old queue state remains.

The worker stops draining when it makes no terminal progress and revisits durable
state on a periodic sweep. A delayed queue is not an empty queue. Waiting for a
new producer signal alone could leave delayed work asleep forever. Busy-looping
on delayed rows would waste resources while accomplishing nothing.

## Why ownership needs more than a name

Worker A claims project P and pauses. Its 30-second lease expires. Worker B
claims P. Now A resumes. Merely writing `Owner=A` on a row at claim time cannot
prevent A from saving work after ownership moved to B.

The lease token includes project ID, a random worker owner ID, and an increasing
generation. Every processing, retry, and dead-letter transaction checks those
values and the expiry before modifying work. It acquires a database write on
the lease row and keeps the transaction protection until commit.

```text
10:00:00  A claims P, generation 7, expires at 10:00:30.
10:00:31  B reclaims P, generation 8.
10:00:32  A asks to write using owner A / generation 7.
          No current lease matches. A changes nothing.
10:00:33  B writes using owner B / generation 8.
          The transaction processes and acknowledges the work together.
```

Read that timeline again using a library analogy: A's temporary checkout card
expired. B received a new card number. A's old card does not regain authority
just because A wakes up holding a copy of it. The generation is the changing
card number; expiry tells the system when a replacement may be issued.

## Find each protected boundary

| File/method | Protected work |
| --- | --- |
| [IngestionLeaseService.FenceAsync](../../src/Pulse.Infrastructure/Services/IngestionLeaseService.cs) | Current owner/generation/expiry and project pause check inside a transaction |
| [CaptureService.IngestAndAcknowledgeAsync](../../src/Pulse.Infrastructure/Services/CaptureService.cs) | Identity changes, definitions, event, processing item, and queue removal |
| `IngestionProcessor.MoveToDeadLetterAsync` | Exactly one queue removal, one dead letter, and its processing state |
| Retry branch in `ProcessLeasedRowAsync` | Conditional attempt/due/error update after failed processing |

Checking once at the start of a batch would leave later rows unprotected.
The worker renews between bounded row operations and stops when it loses the
lease. A lease expiring during an already-protected transaction does not allow
another writer to slip into its middle: the new claimant waits for SQLite write
protection and must recheck afterward.

Do not hold these transactions across network calls or deliberate waits in
production. Tests may install barriers to expose races, but the production work
inside the protected transaction must stay short and local.

## Fairness is separate from exclusivity

Exclusive ownership prevents two workers from concurrently changing one
project's identity graph. It does not, by itself, ensure other projects make
progress. The processor selects bounded due projects using last-served order,
processes at most 50 rows per project, and caps the overall batch at 200 attempts.
Different projects can belong to different workers.

SQLite still serializes database writes. A second worker adds recovery and
project-level scheduling; it does not imply twice the throughput. Supported
deployment uses instances sharing the same supported local SQLite environment,
not a network-filesystem claim. Compare contention and timings using the same
workload before making performance claims.

All old unfenced workers must stop before migrating and deploying this version.
An old process that ignores the lease can defeat every guarantee described
here. Returning to one process does not justify removing fences while another
instance might still be alive. Clocks must be sufficiently aligned for the
lease-duration assumptions; test clocks make the intended boundary explicit.

## Practice without waiting for real seconds

Open [IngestionRetryScheduleTests](../../tests/Pulse.Tests/Infrastructure/IngestionRetryScheduleTests.cs).
The fake clock advances directly to one tick before due time and then to equality.
That tests scheduling policy without introducing multi-second sleeps into the
correctness proof. New contexts simulate process restarts by discarding memory
while retaining the same database file.

Open [IngestionLeaseTests](../../tests/Pulse.Tests/Infrastructure/IngestionLeaseTests.cs).
Follow `PausedOldOwnerCannotMutateAfterReassignment_AndNewOwnerProcessesOnce`.
Before looking at assertions, predict event count, person count, queue count,
and processing-item state after A's rejected attempt. Then predict the result
after B runs. Every prediction should follow from one transaction boundary.

Try these teach-back prompts on separate days:

1. Explain why a malformed payload and a locked database deserve different responses.
2. Explain why attempt count and queue age should not use the same timestamp.
3. Draw A pausing outside a transaction and B receiving a newer generation.
4. Point to the database write that makes A's stale token harmless.
5. Explain how delayed rows can be overtaken and why strict causal ordering is not promised.
6. Explain why a crash after event insertion but before commit must preserve the queue row.
7. Separate the claim “no duplicate committed event from a stale worker” from the claim “faster processing.”

The transferable skill is identifying the smallest authoritative state change
that settles a race. Here it is a conditional lease write held through the
business transaction. In Session 15 it was a conditional flag revision update.
Different features, same habit: locate the database condition that makes an
otherwise plausible interleaving impossible.
