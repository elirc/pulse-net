# Pulse implementation bootcamp

This bootcamp follows the implementation of all 75 AstraDocs stories. Its main
deliverable is learning: each feature should leave you with an explanation,
a small exercise, evidence you can reproduce, and a path to the real code.

The original backlogs describe target behavior. [stories.json](stories.json)
is the implementation ledger: `planned`, `in_progress`, `implemented` (not yet
verified), and `verified` are different states. A story is verified only when
its acceptance evidence is recorded. Nothing is complete just because it is
listed here.

## Start here

If this is your first session, do one small loop before trying to understand the
whole application:

1. Open [Session 1](01-practice-tools.md) and write its five-events/one-person
   prediction in a copy of the [practice journal](practice-journal-template.md).
2. Read the example and locate the named script. Do not change code yet.
3. Run the focused example only against a disposable local database, or read the
   expected evidence first if you have not started the API.
4. Compare the observation with the prediction and explain one difference in
   ordinary language.
5. Continue through Sessions 2–25 in index order. Each lesson groups one or more
   of the 75 stories around a reusable engineering idea, so 25 lessons still
   cover the full ledger.

You can begin by reading; a running application is needed only when a lesson asks
you to reproduce behavior. “I have not run this yet” is an honest result. Keep it
separate from “the test passed” or “the feature is verified.”

## How to use a session

Use the [learning path](learning-path.md) for three passes through each lesson,
progress milestones, a worked study session, and spaced review.

1. Read its concrete example and predict the result before running anything.
2. Follow the named files from input to response. Sketch the path yourself.
3. Reproduce the example or focused tests in an isolated practice database.
4. Change one input and explain why the result changes.
5. Answer the teach-back question without copying the explanation.
6. Revisit the same idea the next day using a different feature.

Keep four activities distinct:

| Activity | What you produce | What it establishes |
| --- | --- | --- |
| Read | A plain-language statement of the requested behavior | You found the contract; it does not prove the implementation |
| Predict | Expected status, rows changed, and rows preserved | Your current model is specific enough to challenge |
| Reproduce | A command, fixture, observed result, and assertion | The behavior occurred in that environment under those conditions |
| Explain | The responsible boundary and why the evidence catches a mistake | You can transfer the idea when an input or timing condition changes |

## Delivery order

| Stage | Stories | Practice |
| --- | --- | --- |
| 1 | US-01–US-06 | Safe practice scripts and accessible lesson controls |
| 2 | US-07–US-25 | Authorization, validation, filtering, and pagination |
| 3 | SR-01 | Versioned schema and preserving existing databases |
| 4 | SR-02–SR-05, SR-16 | Roles, audit evidence, concurrency, token restrictions |
| 5 | MID dashboard/insight/cohort/flag stories | Cross-layer features with explicit contracts |
| 6 | SR-07–SR-11, MID ingestion stories | Admission, retries, receipts, and worker ownership |
| 7 | SR-12–SR-15, MID export stories | Durable jobs, snapshots, retention, erasure |
| 8 | SR-06, SR-17–SR-20, remaining MID stories | Scheduling, tracing, bounded analytics, sessions, alerts |

This is dependency order, not an estimate of how fast you must learn. Later
features can change earlier contracts, so integration tests and lessons must
be updated together. The ledger links each completed story to its final code
and evidence.

## Journal and lessons

- [Implementation journal](journal.md): decisions, findings, results, and next steps.
- [Story map](story-map.md): all 75 stories with plan, lesson, implementation, and current-status links.
- [Capstone review](capstone-review.md): connect retries, worker crashes, exports, erasure, bounded queries, and alerts in one worked timeline.
- [Verification record](verification.md): actual pass/fail evidence and its limits.
- [Your practice journal template](practice-journal-template.md): predictions, experiments, corrections, and teach-back notes.
- [Review cards](review-cards.md): revisit each concept using small questions and code-location exercises.
- [Session 1: practice tools](01-practice-tools.md): parameters, transport encoding, and lesson state.
- [Session 2: scoped queries](02-scoped-queries.md): validation, filtering, paging, and evidence.
- [Session 3: database upgrades](03-database-upgrades.md): schema history, preservation, locks, and recovery.
- [Session 4: dashboard composition](04-dashboard-composition.md): shared references, atomic batches, and request-local reuse.
- [Session 5: insight lifecycle](05-insight-lifecycle.md): preview, replacement, relative dates, usages, and guarded deletion.
- [Session 6: portable templates](06-portable-templates.md): versioned documents, reference mapping, and bounded imports.
- [Session 7: comparing analytics](07-comparing-analytics.md): half-open periods, percentage math, and aligned event series.
- [Session 8: project roles](08-project-roles.md): permission layers, response credentials, migration defaults, and competing administrators.
- [Session 9: person activity](09-person-activity.md): canonical identity, tied timestamps, query-bound cursors, and UTC summaries.
- [Session 10: cohort lifecycle](10-cohort-lifecycle.md): live rules, no-write previews, membership snapshots, and atomic set replacement.
- [Session 11: flag decisions](11-flag-decisions.md): shared explanations, measured read reuse, draft races, and conditional key rotation.
- [Session 12: admission and recovery](12-admission-and-recovery.md): shared parsing, zero-write previews, replay diagnostics, and partial progress.
- [Session 13: export lifecycle](13-export-lifecycle.md): new retry attempts, metadata queries, conditional deletion, and exact document bytes.
- [Session 14: discovery and observation](14-discovery-and-observation.md): honest totals, typed properties, scan budgets, and operational thresholds.
- [Session 15: trustworthy flag changes](15-trustworthy-flag-changes.md): conditional edits, atomic evidence, forward-only restoration, and durable scheduling.
- [Session 16: admission identity and receipts](16-admission-identity-and-receipts.md): bounded client retries, shared processing obligations, receipt lifetimes, and capacity.
- [Session 17: retries and worker ownership](17-retries-and-worker-ownership.md): durable backoff, failure categories, lease generations, and stale-worker rejection.
- [Session 18: recoverable exports](18-recoverable-exports.md): durable cancellation, contested publication, immutable inputs, and restart experiments.
- [Session 19: event retention](19-event-retention.md): fixed cutoffs, versioned policies, bounded deletion, and truthful receipt retirement.
- [Session 20: restricted personal tokens](20-restricted-personal-tokens.md): current roles, project allowance, named scopes, expiry, and response redaction.
- [Session 21: person erasure](21-person-erasure.md): a data inventory, project pause, suppression, bounded cleanup, and explicit review.
- [Session 22: ingestion tracing](22-ingestion-tracing.md): durable parent contexts, distinct attempt spans, replay links, and diagnostic data boundaries.
- [Session 23: bounded trends](23-bounded-trends.md): complete-result semantics, streaming projections, independent budgets, and cancellation.
- [Session 24: person sessions](24-person-sessions.md): gap equality, canonical identities, deterministic ordering, and window boundaries.
- [Session 25: hourly alerts](25-hourly-alerts.md): durable evaluation, notification delivery, catch-up limits, and per-user read state.

## Verification rules

Use the repository's [testing policy](../../docs/testing.md). Tests run against
isolated databases; practice data should never be your only copy of important
data. Record actual failures and environmental blockers. A successful build
is useful evidence but does not establish the behavior of a feature.

For the safest first experiment, run a focused automated test: the fixtures
create private temporary databases and remove them afterward. The learning demo
targets a running API and intentionally keeps its generated practice project for
inspection, so start that API with a new disposable database. Check the active
connection string before running a deletion, retention, migration, or restore
exercise. Never aim a practice command at a shared environment or at the only
copy of data you care about. Source rollback cannot restore deleted database rows.

Run `node scripts/check-bootcamp.mjs` from the repository root to check the
75-story ledger and local lesson links. It checks documentation consistency;
the feature test evidence remains a separate responsibility.

Regenerate the human-readable story map after changing ledger status or links:

```text
node scripts/render-story-map.mjs
```

Check that committed output still matches the ledger without writing it:

```text
node scripts/render-story-map.mjs --check
```

Both commands locate the repository from the script file, so they also work
when invoked with an absolute script path from another working directory.

An implementation checkpoint should answer four questions: what changed, why
it belongs in this layer, what test can catch a mistake, and what limitation
remains. These questions are the bridge from following instructions to owning
a feature independently.
