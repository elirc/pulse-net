# Performance: measure a question

**Outcome:** connect a slow request to data volume and query shape, then
show that an optimization preserves semantics.

## Follow the data across the SQL boundary

[QueryService](../../src/Pulse.Infrastructure/Services/QueryService.cs)
filters a slice in SQL and performs parts of analytics in memory. Find the
`ToListAsync` calls. For each, write how many rows and which fields might be
loaded for a one-day query versus a full-year query.

The new project metrics query aggregates pending count and oldest enqueue
time in SQL. It does not load payloads to count them. That is a useful small
example of matching the query output to what the caller needs.

EF's guidance covers indexes, projections, bounded result sets, and query
plans in [efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying).

## Build a measurement note

Record dataset size and distribution, hardware, build configuration, warmup,
query parameters, repeated durations, and allocations if measured. Keep
semantic assertions alongside the measurements. A faster funnel with wrong
ordering is not an improvement.

Use a disposable local database. Seed once per dataset and keep the input
stable between baseline and changed measurements. Avoid reporting a single
best timing as the typical experience.

## Exercise: a query-plan investigation

1. Pick one trend query with a time range and event name.
2. Inspect the relevant composite indexes in `PulseDbContext`.
3. Obtain the generated SQL in a local diagnostic session and inspect it
   with SQLite's `EXPLAIN QUERY PLAN` using an available SQLite client.
4. Compare a query with the event predicate to one without it.
5. Propose an index or projection change and measure read and write costs.

If you add an index, create and review a checked-in migration and test the
transition from the previously supported schema. Ordinary startup only verifies
that the database is current; it does not create or upgrade it. Use a fresh
disposable database for the experiment, then rehearse `db status` and
`db upgrade` on a recoverable copy by following the
[database-upgrade runbook](../runbooks/database-upgrades.md).

## Capacity reasoning

Suppose capture accepts 120 events/second and processing sustains 100.
The backlog grows by 20/second: 72,000 rows/hour if rates stay constant.
Queue depth tells you how much work remains; oldest age helps describe how
long the oldest accepted work has waited. Neither alone tells you the cause.

**Stretch:** compare one large tenant with many small tenants. Does a global
queue make latency fair? Design a benchmark before changing scheduling.

**Done when:** you can show a baseline, explain the suspected bottleneck,
measure the change, preserve query results, and state the new write or
storage cost. Avoid proposing a cache until you can explain invalidation.
