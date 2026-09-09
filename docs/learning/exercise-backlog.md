# Exercise backlog

Choose one task at a time. Before code: write the contract, the risky edge,
and the smallest test that could disprove your design. Each task ends with
tests appropriate to its risk, documentation, and a short review note.
Difficulty describes expected independence, not the number of lines.

## Foundation tasks

| # | Task | Acceptance criteria | Start in |
| --- | --- | --- | --- |
| 1 | Trace and vary the learning demo | Explain all three credentials; change event and query; predict count | scripts/learning-demo.ps1 |
| 2 | Add a time-bucket boundary case | Assert just-before and exactly-on-boundary values; explain UTC handling | TimeBucketTests |
| 3 | Add a property-merge example | Establish overwrite versus set-once behavior for an existing value | PersonPropertyMergerTests |
| 4 | Document one auth denial with a test | Valid user, wrong project, no data or mutation | AuthzMatrixTests |
| 5 | Filter dead letters by error text | Optional filter; bounded length; project isolation; unchanged omitted behavior | IngestionEndpoints |
| 6 | Add stable tie-breaking to dead-letter listing | Equal FailedAt timestamps have deterministic ID order; bounded result | IngestionEndpoints |

## Independent feature tasks

| # | Task | Acceptance criteria | Start in |
| --- | --- | --- | --- |
| 7 | Add cursor pagination to dead letters | Equal timestamps, invalid cursor, end of list, project mismatch policy | ExportService as reference |
| 8 | Classify malformed nested properties as permanent | No wasted retries; preserve useful error; update documented Attempts semantics if changed | IngestionPipeline |
| 9 | Add a configurable queue-age health threshold | Validate settings; injected time; empty queue and boundary tests | Health route and operations service |
| 10 | Add replay audit history | Actor, original ID, outcome, new queue ID; transactional consistency; no sensitive payload logging | IngestionOperationsService |
| 11 | Add safe diagnostic error codes | Stable external code; internal detail policy; test unknown exceptions | Worker and dead-letter contracts |
| 12 | Add an ingestion status CLI/script | Member credentials as inputs; deadline; separate pending and failed outcomes; no secrets printed | learning-demo.ps1 |
| 13 | Consolidate duplicate wake-up signal code | Preserve cancellation, dropped-notification, and periodic-sweep behavior; justify abstraction | IngestionSignal and ExportSignal |

## System ownership tasks

| # | Task | Acceptance criteria | First design question |
| --- | --- | --- | --- |
| 14 | Project-scoped event idempotency | Concurrent duplicate request, different-payload conflict, restart durability, retention policy, migration | Is the ID per event or per batch? |
| 15 | Ordered retry processing | Failing earlier update cannot violate the chosen ordering guarantee; unrelated work policy is explicit | What is the ordering partition? |
| 16 | Scheduled retries with backoff | Injected clock; max attempts; due-time boundaries; no busy loop; schema upgrade | What delay and jitter policy fits the load? |
| 17 | Recover interrupted export jobs | Simulated restart from Running; expiry; bounded retries; duplicate-output policy | Who owns a job and when does ownership expire? |
| 18 | Introduce migrations | Upgrade existing data; verify new install; deployment and rollback rehearsal | How will old and new app versions coexist? |
| 19 | Optimize a measured analytics query | Reproducible dataset; query plan; timings; same answers; read/write cost | What evidence identifies the bottleneck? |
| 20 | Unify capture auth and rate-limit identity | Body/header precedence specified; mismatched keys tested; invalid-key handling; no unbounded body buffering | At what middleware stage is identity trustworthy? |

## Hints without full solutions

For 7, study the export cursor tie-breaker and consider what new arrivals do
between pages. For 10, write the audit row inside the same transaction as
the replay move. For 14, an application-level existence check alone does not
resolve races; investigate a database uniqueness constraint. For 17, a
`Running` state needs an expiry or explicit recovery path to survive a crash.

For each advanced task, write an ADR before implementation. Ask for feedback
on one disputed tradeoff, then record why you kept or changed your decision.
