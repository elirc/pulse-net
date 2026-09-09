# The rest of Pulse, one feature at a time

You do not need to implement these today. Recognize the problem each feature
solves, then follow its linked starting point when a task touches it.

## Analytics: three different questions

| Feature | Mara asks... | Read first |
| --- | --- | --- |
| Trend | How many pageviews happened each day? | [QueryService.TrendAsync](../src/Pulse.Infrastructure/Services/QueryService.cs) |
| Funnel | How many people signed up, then purchased within the allowed window? | `FunnelAsync` in the same file |
| Retention | For a cohort entering the selected window, who was active on later days? | `RetentionAsync` in the same file |

A trend groups matching activity into time buckets. A funnel cares about
ordered steps per person. Retention compares later activity with a cohort's
entry day; here that entry is the first qualifying event inside the query
window, not necessarily the person's first event ever.

The query engine filters a slice in SQL and performs further calculations
in memory. These are different stages; loading a large slice still costs memory.

## Saved insights and dashboards

An **Insight** saves a query definition. A **DashboardTile** points to a saved
insight, and a **Dashboard** groups tiles. Refresh executes the tile queries.

Think: recipe -> item on a menu -> whole menu. The analogy concerns grouping;
a saved insight is not permanently frozen result data.

Start at [DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs)
and follow refresh into
[InsightRunnerService](../src/Pulse.Infrastructure/Services/InsightRunnerService.cs).

## Cohorts and feature flags

A cohort is a group of people. Static cohorts store selected members;
dynamic cohorts calculate members from rules. Read
[CohortService](../src/Pulse.Infrastructure/Services/CohortService.cs).

A feature flag decides whether a visitor gets a feature or variant. The
evaluation path checks active status, targeting, and deterministic rollout.
It does not randomly reroll on every request. Read
[FeatureFlagService](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs)
and [FeatureFlagHasher](../src/Pulse.Domain/FeatureFlagHasher.cs).

## Registry, annotations, and deletion

Event/property definitions describe names and types observed during ingest.
Annotations attach dated notes that can appear with trends. These operations
live in [DataManagementEndpoints](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs).

[PersonEndpoints](../src/Pulse.Api/Endpoints/PersonEndpoints.cs) supports person
lookup and removal of associated stored data. Before extending deletion,
inventory every related data path rather than assuming one row is the entire person.

## Exports and ingestion operations

[ExportService](../src/Pulse.Infrastructure/Services/ExportService.cs) returns
export data. Async export jobs save pending work; `ExportWorker` invokes
[ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs).
The latter changes jobs from Pending to Running to Completed or Failed.
Interrupted Running jobs currently need a recovery design; automatic restart
recovery is not implemented.

Ingestion metrics show backlog information. Dead-letter inspection and
single-letter replay help investigate and retry failed activity. Replay
appends work to the queue and can fail again; it is not an automatic repair.

**Repeat the map:** capture collects; identity connects; queries calculate;
insights save questions; dashboards group questions; flags decide; exports
package data; operations help you see and recover failures.

Next: [learn through tests](11-tests-as-examples.md).
