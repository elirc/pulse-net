# ADR 0008: Queries filter in SQL, evaluate in memory

**Status:** accepted

## Context

Trends, funnels and retention need: time-range + event-name selection,
JSON property predicates (on events *and* on the person who performed them),
cohort membership, interval bucketing, per-person sequence walks. SQLite can
do the first part fast (ticks + composite indexes) but has no useful JSON
predicate pushdown for our opaque `PropertiesJson` blobs, and per-person
funnel traversal in SQL means window-function gymnastics that would be hard
to read and harder to test.

## Decision

`QueryService.LoadEventsAsync` draws the line explicitly:

- **SQL:** `ProjectId` + event-name predicate + timestamp range (and
  `PersonId IS NOT NULL` where person attribution is required). This is the
  selective part — it shrinks millions of rows to the queried slice using
  the indexes.
- **Memory, on that slice:** property-filter evaluation
  (`PropertyFilterEvaluator` over the JSON), person-property and cohort
  filtering, `TimeBucket` bucketing with zero-fill, breakdown grouping
  (top-N, `(none)`, `(other)`), the funnel step walk, and retention triangle
  math.

Pure logic lives in `Pulse.Domain` (no dependencies) so the tricky parts —
bucket truncation, filter operators, merge rules — are unit-testable without
a database.

## Consequences

- Query code is straightforward C# with real names, and its edge cases
  (same-timestamp funnel steps, window boundaries, DST-adjacent bucketing,
  breakdown ties) are asserted directly in tests rather than encoded in SQL.
- Correctness in one place: `/decide` targeting, cohort rules and insight
  filters all reuse the same evaluator — no SQL/C# semantic drift.
- Memory use is bounded by the *time-sliced* event count, not the table; a
  very wide query on a very large project would materialize a large list.
  Acceptable at SQLite scale; the seam to change is `LoadEventsAsync`.
- Person-filtered queries currently load the project's person-property map
  once per query — O(persons) but amortized across all events in the slice.
- Moving to a columnar store later (the PostHog path: ClickHouse) would
  replace the in-memory half with SQL aggregation; the domain-layer semantics
  and their tests would remain the spec.

## In the code

- The seam: `QueryService.LoadEventsAsync`
  (`src/Pulse.Infrastructure/Services/QueryService.cs:256` onward). SQL part:
  `ProjectId`, the name predicate, `Timestamp >= from`, `<= to` or `< to`
  when `endExclusive`, optional `PersonId != null` (lines 266-277),
  projected to `QueryEvent` and materialized (lines 279-281). Without
  filters the slice is returned as-is (lines 283-286).
- In memory: filters split by target (event / person / cohort,
  lines 288-290); person filters load the project's whole
  `Persons` property map (lines 292-298).
- Deterministic funnel tie-break: `OrderBy(Timestamp).ThenBy(stepIndex)`
  (lines 172-173); breakdown ordering by count then ordinal key
  (lines 114-115).

**Check:** name the line where a query over a large project would first
allocate memory proportional to the number of matching events, and the line
where person filters add a cost proportional to the project's persons.
