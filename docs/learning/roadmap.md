# Roadmap: increasing independence

Treat each stage as a set of evidence to earn. The suggested cadence is one
stage over one or two weeks of focused sessions; repeat stages as needed.
Keep a journal entry and a small diff for each practical task.

| Stage | Read | Build or investigate | Exit evidence |
| --- | --- | --- | --- |
| 1. Tool fluency | First session, C# foundations | Run the demo; trace one test; make a small behavior-preserving edit | Explain the request path and your diff unaided |
| 2. Local correctness | C# foundations, testing | Add a domain boundary case and explain why it matters | Show the failing assertion before the fix |
| 3. HTTP ownership | HTTP and authorization | Add a dead-letter list filter | Contract, validation, compatibility, cross-project test |
| 4. Persistence | Data and transactions | Reproduce acknowledgement failure | Database-state evidence before and after rollback |
| 5. Async behavior | Async reliability | Diagnose a poison event and safely replay a recoverable one | Incident note; explain 202 and final outcome |
| 6. Independent delivery | Worked example, exercise backlog | Complete a medium task from a written spec | Reviewed feature with tests and updated docs |
| 7. Performance | Performance chapter | Measure one query and a proposed improvement | Reproducible before/after report and unchanged results |
| 8. System ownership | Design and operations, runbook | Design idempotency, export recovery, or migrations | ADR, failure model, rollout and rollback plan |
| 9. Collaboration | Templates and rubric | Ask for review; review another person's change | Evidence that feedback improved a decision |

## A repeatable study session

Use roughly 10 minutes to recall the previous topic, 15 to read the relevant
code, 30 to implement or investigate, and 10 to explain your result. These
are pacing suggestions, not a timer-based definition of progress.

At the end of a stage, choose a related task with one changed requirement.
If you can only repeat the exact tutorial, revisit the underlying concept.

## Three capstones

**Junior foundation:** add one bounded filter with contract examples,
validation, and tests. Explain each line and debug one failing case.

**Mid-level ownership:** implement event idempotency. Resolve project scoping,
duplicate payload policy, races, migration, and retry semantics. Ship a
reviewable change that another engineer could maintain.

**Senior practice:** propose and prototype recovery for interrupted export
jobs. Compare leases, retries, and output storage choices; demonstrate a
restart failure; describe operational ownership and rollout. Present the
tradeoffs to another engineer and record what their feedback changed.

Use [the rubric](competency-rubric.md) to choose the next skill gap. Do not
skip testing and operations because another feature sounds more interesting.
