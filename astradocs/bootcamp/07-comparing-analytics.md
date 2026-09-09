# Session 7: Counts need a precise question

This session accompanies MID-08 and MID-09. Consult the ledger for their current
verification status.

## Ask two different questions

"Did signups increase compared with the previous week?" needs adjacent periods
and a comparison formula. "How do signup and purchase trends compare on one
chart?" needs aligned series and the existing bucketing rules. Similar input
fields do not automatically mean the questions have the same semantics.

## Draw the period boundaries

For current March 8–15, the equal-duration previous period runs March 1–8:

```text
previous: [March 1, March 8)
current:  [March 8, March 15)
```

The square bracket includes the starting instant. The round bracket excludes
the ending instant. An event exactly at March 8 belongs only to current. One
exactly at March 15 belongs to neither of these queried periods.

Imagine consecutive boxes on a conveyor belt. A boundary item goes into the
box beginning there, rather than both boxes. In SQL terms, each count uses
`Timestamp >= start && Timestamp < end`.

Read [AnalyticsCompositionService](../../src/Pulse.Infrastructure/Services/AnalyticsCompositionService.cs)
and find those operators. Counting happens in SQL without loading event JSON.
Rows with a null PersonId still count as event rows. This is an event metric,
not a unique-person metric.

Comparison requires explicit bounds, positive duration, and at most 90 days.
Computing a previous start can underflow the supported timestamp range; the
endpoint reports a validation error instead of an unhandled arithmetic failure.

## Define the formula before choosing a display

```text
delta = currentCount - previousCount
percentChange = delta * 100 / previousCount
```

| Current | Previous | Delta | Percent change |
| --- | --- | --- | --- |
| 15 | 10 | 5 | 50 |
| 0 | 10 | -10 | -100 |
| 2 | 3 | -1 | -33.33 |
| 5 | 0 | 5 | null |
| 0 | 0 | 0 | null |

The percentage uses non-integer division and rounds to two decimals, with
midpoints rounded away from zero. A zero previous count has no finite percentage
under this formula. Returning null avoids infinity or an invented 100%; the
counts and delta still communicate useful information.

Explain it twice: five more events is an absolute increase of five. A relative
increase divides by the earlier count, and dividing by zero does not produce
a meaningful finite percentage.

## Reuse trend semantics for multiple series

Multi-trend accepts 1–5 unique trimmed event names, explicit dates, and a named
hour/day/week interval. Names stay case-sensitive. It validates every name
before executing the first query, then calls the existing trend service
sequentially in request order.

Multi-trend inherits the existing trend endpoint's inclusive range. If a
standalone trend counts an event exactly at `to`, the multi-trend series must
count it too. Reimplementing bucketing would create another place for these
semantics to drift. Period comparison deliberately uses different boundaries.

Missing events still get zero-filled buckets. Each series has matching bucket
starts, and annotations appear once at the top level. The calls share a scoped
DbContext, so orchestration stays sequential. The response does not promise
one database snapshot across all queries, and the existing engine's event-slice
materialization cost still applies.

## Do not add unique-person counts across series

Suppose one person signs up and purchases on the same day:

| Series | Person IDs represented | Per-series unique count |
| --- | --- | --- |
| signup | person-A | 1 |
| purchase | person-A | 1 |

The union contains one person. Adding the counts gives two appearances across
series, not two different people. The endpoint preserves each series' existing
unique-person semantics without inventing a combined metric. Ask which set a
count describes before adding its value to another count.

## Trace the request boundary

[AnalyticsCompositionEndpoints](../../src/Pulse.Api/Endpoints/AnalyticsCompositionEndpoints.cs)
uses existing read access: membership or the project's read key. A write key
does not become an analytics credential.

```text
GET  /api/projects/{projectId}/insights/period-comparison?event=signup&from=...&to=...
POST /api/projects/{projectId}/insights/multi-trend
```

```json
{
  "events": ["signup", "purchase", "missing-event"],
  "from": "2026-03-08T00:00:00Z",
  "to": "2026-03-15T00:00:00Z",
  "interval": "day"
}
```

Comparison rejects equal bounds because duration must be positive. Multi-trend
permits equal bounds because it follows the inclusive query contract. A test
should expose this distinction, not hide it inside a shared helper with one
hard-coded boundary policy.

## Use fixtures as an independent calculation

[AnalyticsCompositionTests](../../tests/Pulse.Tests/Api/AnalyticsCompositionTests.cs)
places events just before and exactly at the boundaries. It includes another
project and another event name so an unscoped query cannot pass accidentally.

For hour, day, and week intervals, multi-trend tests compare every series with
a standalone trend using identical inputs. Percentage cases instead compare
with small values calculated on paper. Computing an expected answer by calling
the same new implementation would provide much weaker evidence.

```powershell
dotnet test --filter FullyQualifiedName~AnalyticsCompositionTests
```

## Practice and teach back

1. Assign one event at each boundary to a period on paper.
2. Rewrite both periods with SQL comparison operators.
3. Predict results when the previous count is zero, then three.
4. Reverse the requested event list and predict response order.
5. Use one person in two series and explain why their unique counts cannot
   produce a combined unique-person total by addition.
6. Explain why comparison does not call TrendAsync while multi-trend does.
   Connect your answer to the question each endpoint promises to answer.
