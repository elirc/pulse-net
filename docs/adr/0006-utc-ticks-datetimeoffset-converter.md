# ADR 0006: Store `DateTimeOffset` as UTC ticks

**Status:** accepted

## Context

SQLite has no native date-time type. EF Core's default mapping stores
`DateTimeOffset` as TEXT (ISO-8601 *with the original offset*), and SQLite
compares TEXT lexicographically — so `2026-03-01T10:00:00+02:00` and
`2026-03-01T09:00:00+01:00` (the same instant) don't compare equal, and
range predicates over mixed-offset data are simply wrong. An analytics engine
does almost nothing *but* time-range filtering and ordering, and it must do
it in SQL to keep the working set small.

## Decision

`PulseDbContext` installs a model-wide value converter
(`DateTimeOffsetToUtcTicksConverter`): every `DateTimeOffset` property is
persisted as `UtcTicks` (`long`) and materialized as
`new DateTimeOffset(ticks, TimeSpan.Zero)`.

Timestamp columns are covered by composite indexes
(`(ProjectId, Name, Timestamp)`, `(ProjectId, PersonId, Timestamp)`), and
export cursors embed the same ticks value so cursor comparisons and column
comparisons can never disagree.

## Consequences

- `WHERE Timestamp >= @from AND Timestamp <= @to` and `ORDER BY Timestamp`
  become plain integer comparisons — correct across offsets and fast under
  the indexes.
- The original client offset is **not round-tripped**: everything reads back
  as `+00:00`. For analytics this is the desired semantics (all bucketing,
  retention and cohort math is defined in UTC); clients that care about local
  time carry it in event properties.
- The columns are opaque `long`s in raw SQL — debugging queries need
  `DateTimeOffset(ticks)` mental math or a helper. An accepted ergonomic
  cost, and the converter has dedicated round-trip/ordering tests
  (`DateTimeOffsetConversionTests`) documenting the behavior.
- The converter applies to *every* `DateTimeOffset` in the model
  (`ConfigureConventions`), so no property can accidentally fall back to the
  broken TEXT mapping.
