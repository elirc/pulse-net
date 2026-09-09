# Review findings and learning priorities

This review focuses on ingestion, operational recovery, the query boundary,
and the learning experience. It is not an exhaustive security or production
readiness audit. Use the findings as a starting point for evidence-driven work.

The [original validation record](validation.md) preserves the earlier review's
results and environmental blockers. Subsequent implementation and current
evidence live in the [bootcamp verification record](../../astradocs/bootcamp/verification.md)
and [75-story map](../../astradocs/bootcamp/story-map.md).

## Strengths to preserve

The project separates pure domain rules from persistence and HTTP. It has
real SQLite integration tests, explicit project membership checks, durable
queue storage, and ADRs explaining several tradeoffs. These are useful
examples to study before introducing more abstractions.

## Addressed in the initial review

| Finding | Consequence | Change and evidence |
| --- | --- | --- |
| Capture committed before queue deletion | Crash could cause the same queue row to produce another event | Shared transaction in CaptureService; acknowledgement-failure test checks rollback and retry |
| Dead-letter insertion and queue deletion were separate | Failure could leave both records and duplicate failure records on retry | Atomic move; failed-delete test proves rollback |
| Operations exposed global counts only | A member could not inspect their own backlog age | Member-only project metrics with injected clock and project isolation tests |
| Failed events could only be inspected | No application path to retry after repairing a dependency or bug | Single-letter atomic replay; invalid payload, repeated call, concurrency, and rollback tests |
| Docs concentrated on routes and architecture | Beginners had no sequence, exercises, or skill checkpoints | Learning chapters, roadmap, rubric, labs, worked example, and a PowerShell demo |

## Findings addressed by the story implementation

The initial review also identified missing client retry identities, worker
ownership, export restart recovery, and schema migration. Those are implemented
now. Study the corresponding lessons to see how a review finding becomes a
contract and a test:

- [Admission identity and receipts](../../astradocs/bootcamp/16-admission-identity-and-receipts.md): project-scoped client UUIDs deduplicate matching retries for seven days.
- [Worker ownership](../../astradocs/bootcamp/17-retries-and-worker-ownership.md): each mutation must prove the current owner and generation inside its transaction.
- [Recoverable exports](../../astradocs/bootcamp/18-recoverable-exports.md): expired claims can be recovered; stale attempts cannot publish.
- [Database upgrades](../../astradocs/bootcamp/03-database-upgrades.md): startup verifies; explicit offline commands adopt or upgrade with preservation checks.
- [Bounded trends](../../astradocs/bootcamp/23-bounded-trends.md): a separate route rejects excessive work; legacy query routes still have their original broader behavior.

## Remaining constraints to practice on

| Priority | Evidence in code | Why it matters | Next exercise |
| --- | --- | --- | --- |
| Before assuming unlimited retry identity | Client UUID deduplication expires after seven days and is optional | Retries without a retained matching key can create new work | Write a client retry/lifetime policy and test the expiry boundary |
| High for ordered identity updates | Eligible rows omit retries whose persisted due time is in the future | Later updates can overtake an earlier failed event | Per-identity ordering policy |
| Before public operation | Program partitions capture limiting by header key or IP; capture also accepts body keys | Body-key clients share IP limits; header values can differ from the authenticated body key | Consistent authenticated rate-limit identity |
| Before public operation | Global metrics is anonymous; dead letters preserve payloads behind project authorization | Exposure and diagnostic-data retention need a deliberate policy | Review diagnostics access and retained payload lifetimes |
| As datasets grow | Legacy QueryService materializes filtered slices | Wide ranges can consume substantial memory even though the new bounded route rejects excessive work | Compare query plans and measured allocations across both contracts |
| Before treating counters as accounting | Counters are updated in process after committed row outcomes | Restart or a crash between commit and increment loses telemetry | Use durable receipt outcomes for supported accounting questions |
| Before high read concurrency | Protected project reads take SQLite's reserved writer lock | Reads serialize with writers and each other to enforce erasure pause ordering | Measure contention before designing a different database or read protocol |

These are visible design limitations or code-supported failure scenarios,
not claims that every one has been reproduced in a running deployment.

## How to review your next change

Write down the invariant, identify every commit point, and follow an
unauthorized caller through the endpoint. Then try a retry, a cancellation,
and a restart at the least convenient moment. Choose tests based on those
risks. Keep the remaining limitation visible in the PR and documentation.
