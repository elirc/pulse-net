# Design judgment and operational ownership

**Outcome:** propose a change another engineer can review, deploy, observe,
and recover from.

## Refactor around a responsibility

The ingestion endpoints handle HTTP and membership checks. The operations
service handles project metrics and replay's database operation. The capture
service owns the event transaction. These boundaries let you test failure
behavior without booting HTTP while keeping authorization visible at the edge.

Do not add interfaces for every class as a ritual. Ask whether there are
multiple implementations, a useful external boundary, or a testing need that
cannot be met simply. The injected clock is useful because time changes
behavior and tests need control over it.

## Write alternatives before choosing infrastructure

If asked to support multiple API instances, compare: one designated worker,
database leases, and an external queue with partitioning. Evaluate failure
recovery, duplicate processing, ordering, operational effort, and expected
load. A larger toolchain does not automatically solve those requirements.

Read [ADR 0009](../adr/0009-atomic-ingestion-and-replay.md), then write an ADR
for event idempotency or export recovery using [the template](templates.md).
Include what would cause you to revisit the decision.

## Operations is part of the feature

Replay is useful only if an operator can decide when it is appropriate.
The [ingestion runbook](../runbooks/ingestion.md) connects symptoms, evidence,
action, and verification. Practice explaining why increasing retries cannot
repair malformed JSON.

Define an observable objective, for example: "In this load experiment, 99%
of accepted valid events become queryable within five seconds." This is an
exercise target, not an established Pulse service guarantee. You need event
acceptance and visibility timings to measure it; a global counter is not enough.

## A feature review beyond correctness

Ask who can call it, what happens on repeated calls, how much work one request
can cause, what state survives restart, how failures are diagnosed, and how
an older client behaves after deployment. For schema changes, describe
upgrade, rollback, and compatibility before writing new entity properties.

The current project has checked-in EF migrations and verify-only startup.
Startup refuses missing, legacy, pending, unsupported, or structurally unknown
databases instead of changing them. Operators stop writers and use `db status`,
then `db adopt-legacy` for the recognized baseline or `db upgrade` for an empty
or pending database. Plan every schema change with preservation, rollback, and
recovery evidence from the
[database-upgrade runbook](../runbooks/database-upgrades.md).

## Senior practice without pretending to have a team

Write a one-page design and ask someone to challenge it. Give another learner
a small task with context and acceptance criteria. Review their reasoning
before rewriting their code. Record the feedback that changed your decision.

**Done when:** you can state the requirements, rejected alternatives, failure
model, evidence, rollout, recovery, and unresolved risks in plain language.
Maintainability includes whether another engineer can safely change your work.
