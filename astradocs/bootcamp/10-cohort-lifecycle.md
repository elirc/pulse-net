# Session 10: a rule, a preview, a snapshot, and a set

Stories: MID-12 through MID-15. Four audience features revisit the same data in
different ways. The [ledger](stories.json) and [verification record](verification.md)
separate written implementation from executed acceptance evidence.

## Four requests that sound similar

An analyst asks for “everyone on the pro plan.” That sentence can mean four
different operations. Ask what should remain stored after the request finishes.

| Operation | What it reads | What it stores | What changes tomorrow? |
| --- | --- | --- | --- |
| Preview rules | Current people/events | Nothing | Repeating it can return a different audience |
| Replace dynamic rules | One saved cohort | A new rule array under the same ID | Membership follows the new rule and changing data |
| Snapshot dynamic cohort | Current evaluated members | A new static cohort and member IDs | Member profiles can change; the rule no longer decides membership |
| Replace static members | One static cohort and supplied IDs | The requested member set | Another edit or person deletion can change the set |

Read the table twice, once by row and once by the “what it stores” column. The
differences are product behavior, not merely different endpoint names.

An everyday analogy: a rule is a recipe for making a guest list, a preview is
looking at today's result, and a snapshot is writing today's names on paper.
Changing a person's circumstances changes the next recipe result. It does not
rewrite the paper. Even on paper, the person's address or preferences can still
change: freezing membership does not freeze the person's profile.

## One evaluator, one evaluation time

Open [CohortService](../../src/Pulse.Infrastructure/Services/CohortService.cs).
`EvaluateRulesAsync` accepts parsed rules, project ID, and an explicit evaluation
timestamp. Stored dynamic cohorts parse their saved JSON and call it. Preview
and snapshot call the same method through
[CohortEditingService](../../src/Pulse.Infrastructure/Services/CohortEditingService.cs).

Rules combine with AND. If the property rule matches people A/B and the event
rule matches B/C, the answer is B. Sampling before the intersection is wrong:
sampling only A from the first rule would incorrectly lose B entirely.

Preview computes the full result, sorts GUIDs using the defined .NET GUID order,
then takes the requested sample. The count is the full set size. The sample is
deterministic; it is not random or statistically representative.

Why pass time explicitly? Consider two “performed an event in the last day”
rules. Reading the clock separately can put their lower boundaries at different
instants. The caller takes one reading and both rules use it. The extraction
preserves the old rule semantics: the lower bound is inclusive and there is no
new upper bound excluding future-dated events. A refactor should not silently
change an existing audience while adding a preview feature.

## Validation is a candidate boundary

[CohortEditingEndpoints](../../src/Pulse.Api/Endpoints/CohortEditingEndpoints.cs)
checks authorization before invoking work. `CohortCandidateRules` requires a
JSON array of 1–10 rules whose UTF-8 representation is at most 32 KiB, then
delegates operator/kind semantics to the existing parser. Preview also limits
sample size to 1–50. A short returned list does not imply a cheap evaluation:
property rules still inspect the project's person properties in memory.

Replacement validates the entire candidate before assigning `RulesJson`. If
the first rule is valid and the second is invalid, the old array remains stored.
There is no “partly updated cohort.” The ID, type, name, and creation time stay
the same, so a feature flag targeting that cohort sees the new audience on its
next evaluation without needing its own reference rewritten.

This repeats the saved-insight lesson: a stable identifier can refer to a
changing definition. Deleting/recreating an object with the same display name
does not preserve references to its old ID.

## Snapshot is a transaction, not a sampled preview

Snapshot begins its transaction before loading the source and evaluating the
rules. It requires a dynamic source, computes the complete set, rejects more
than 1,000 matches, and checks that every resulting person exists in this
project. It then inserts one static cohort with `RulesJson="[]"` and unique
member rows. One clock reading supplies evaluation and creation time.

The 1,001st match is a conflict, not permission to silently save the first 1,000.
An empty set is a valid empty static cohort. An unexpected dangling event
PersonId that appears in an evaluated audience produces a conflict; it is not
quietly removed from the answer. These decisions make the copy's meaning exact.

The copy and its members commit together. The source is unchanged. A later
property edit can remove a person from the dynamic source while that person's
ID remains in the static copy. Person deletion may subsequently remove static
membership; this feature is not an immutable archive of historical people.

## Set replacement, explained three ways

Existing members are A/B/C. Desired members are B/C/D.

- Remove A, add D, retain B/C. The result contains three people.
- In set notation: additions = desired minus existing; removals = existing minus
  desired; unchanged = intersection; total = desired set size.
- In code: `Except`, `Except`, `Intersect`, then one transaction containing the
  selected bulk delete and the inserts.

Raw input is capped at 1,000 IDs **before** deduplication. Repeating one ID
50,000 times is still an oversized request. Within that cap, duplicates collapse
to one member. Null/missing input is invalid; an explicit empty array means clear
the audience. Unknown and foreign IDs produce the same generic 400 response.

All requested people are validated before deletion. A database failure can
still occur after deletion, so validation alone is insufficient. `ExecuteDelete`
runs immediately; the explicit outer transaction includes it and the subsequent
`SaveChanges`. A rollback restores the previously removed rows.

## Read the failure tests, not only the happy path

[CohortEditingTests](../../tests/Pulse.Tests/Api/CohortEditingTests.cs) exercises
preview equivalence, flag targeting after rule replacement, snapshot behavior
after a profile change, repeated set replacement, invalid input, and access.

[CohortEditingTransactionTests](../../tests/Pulse.Tests/Infrastructure/CohortEditingTransactionTests.cs)
installs a trigger in an isolated SQLite database that aborts a membership insert.
For replacement, the old membership deletion has already executed. After the
exception, the test checks that the original membership is still present.
For snapshot, it checks that no new cohort or member rows remain.

The trigger is a deliberately injected storage failure, not production schema.
The test's value lies in observing persisted state after failure. Merely expecting
an exception would not establish atomicity.

These operations do not promise to merge two concurrent target lists. SQLite
serializes successful writes; the last committed replacement defines the set.
Contention may fail the entire operation. A future retry must repeat the whole
transaction using fresh reads, not just repeat the final insert step.

## Practice ladder

1. List the expected intersection of two rule results before running preview.
   Use a sample limit of one and explain why the count may still be larger.
2. Trace one cohort-targeted flag to its saved cohort ID. Replace the cohort
   rules and predict the next decision without editing the flag.
3. Draw which rows a snapshot inserts. Cross out the idea that it duplicates
   Person records; it stores references to existing people.
4. Calculate added/removed/unchanged for A/B/C → B/C/D, then for repeating
   B/C/D → B/C/D, then for clearing B/C/D → empty.
5. Find the transaction start relative to `ExecuteDeleteAsync`. Explain what
   the failure-injection test would observe if that delete sat outside it.
6. Explain why a 32 KiB rule document and a sample limit do not bound the number
   of person rows evaluated. Identify the relevant query in the service.

Teach back tomorrow: explain the difference between previewing, updating a
recipe, and saving its current result. Use the same person whose plan changes
from pro to basic, and predict all four operations' behavior.
