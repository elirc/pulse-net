# Architecture

pulse-net is a PostHog/Mixpanel-style product-analytics backend: events flow
in through a durable ingestion pipeline, get attached to *persons* via
identity resolution, and are answered by a query engine (trends, funnels,
retention) plus feature-flag evaluation, cohorts, dashboards and exports.

## Layering

```
┌──────────────────────────────────────────────────────────────┐
│ Pulse.Api            HTTP endpoints, contracts (DTOs), auth,  │
│                      validation, background workers            │
├──────────────────────────────────────────────────────────────┤
│ Pulse.Infrastructure EF Core DbContext + services:            │
│                      Capture/Identity/Query/Cohort/Flag/       │
│                      Export services, ingestion & export       │
│                      processors, demo seeder                   │
├──────────────────────────────────────────────────────────────┤
│ Pulse.Domain         Entities and pure logic: property        │
│                      filters, cohort rules, flag hashing,      │
│                      time bucketing, CSV, key generation —     │
│                      zero dependencies                         │
└──────────────────────────────────────────────────────────────┘
```

Dependencies point strictly downward. `Pulse.Domain` has no package
references at all, so everything hash-, filter-, bucket- and merge-related is
unit-testable without a database. `Pulse.Infrastructure` owns persistence and
orchestration; `Pulse.Api` owns HTTP shapes, status codes and the two hosted
workers. Tests (`tests/Pulse.Tests`) cover all three layers, mostly through
the HTTP surface.

## Ingestion pipeline

`POST /capture` is deliberately *not* synchronous:

```
POST /capture ──► validate shape ──► QueuedEvents table ──► 202 Accepted
                                          │        ▲
                              IngestionSignal.Ring │ 1s safety sweep
                                          ▼        │
                                   IngestionWorker (BackgroundService)
                                          │  batches of 200, Seq order
                                          ▼
                                   IngestionProcessor
                                     │ deserialize + re-validate
                                     │ CaptureService.IngestAsync
                                     │   (person resolution, $identify,
                                     │    $set/$set_once, definitions,
                                     │    one transaction per row)
                                     ▼
                          Events / Persons / Definitions tables
                                     │ permanent failure → DeadLetterEvents
                                     │ transient failure → retry ≤ 3
```

Key decisions (see the [ADRs](adr/) for rationale):

- **Durable queue table, not an in-memory channel.** The `QueuedEvents` table
  (keyed by an auto-increment `Seq`) is the source of truth. A crash after
  202 loses nothing; enqueue order — which matters for `$identify` and
  `$set` — is the processing order.
- **The channel is only a doorbell.** `IngestionSignal` wraps a bounded
  `Channel<bool>` of capacity 1 with `DropWrite`: `Ring()` after enqueue wakes
  the worker immediately, and a missed signal only delays work until the
  worker's 1-second periodic sweep. No data rides the channel.
- **Poison classification.** Rows that can never succeed (unparseable JSON,
  failed re-validation, project no longer exists) dead-letter immediately.
  Anything else (an exception inside the capture pipeline) is treated as
  transient and retried up to `IngestionProcessor.MaxAttempts` (3) before
  dead-lettering with the attempt count and final error. Dead letters are
  inspectable per project at `GET /api/projects/{id}/ingestion/dead-letters`.
- **Change-tracker hygiene.** A failed ingest can leave half-tracked entities
  in the shared `DbContext`; the processor calls `ChangeTracker.Clear()` so a
  poison row cannot corrupt its neighbors' `SaveChanges`.
- **Metrics.** `IngestionCounters` (process-lifetime, `Interlocked`) plus a
  live queue count back `GET /api/ingestion/metrics`
  (`pending`/`deadLetters`/`processedTotal`/`deadLetteredTotal`); `/health`
  reports `degraded` when the queue backs up past 10,000 rows.

The export pipeline (`ExportSignal`/`ExportWorker`/`ExportJobProcessor`)
reuses the same signal-plus-sweep pattern for async export jobs: `POST
/api/projects/{id}/exports` inserts a `Pending` job row and returns 202; the
worker pages through data (50,000-row cap), renders CSV/JSON, and stores the
finished document on the job row for download.

## Identity model

Three tables cooperate:

- `Persons` — the person and their JSON properties blob.
- `PersonDistinctIds` — a unique `(ProjectId, DistinctId) → PersonId`
  mapping; a person can own many distinct ids.
- `Events` — each event stores both the raw `DistinctId` it arrived with and
  the resolved `PersonId`.

Rules (PostHog semantics, implemented in `IdentityService` +
`PersonPropertyMerger`):

1. An unseen distinct id creates a person; later events reuse it.
2. `$identify` with `properties.$anon_distinct_id` links the anonymous id to
   the identified id. If only one side exists, the other becomes an alias of
   the same person. If **both** already own different persons, they merge:
   the identified person wins, the loser's distinct ids and historical events
   are repointed (`ExecuteUpdate`), properties merge with winner-wins
   precedence, and the loser row is deleted. Replaying the same `$identify`
   is a no-op.
3. `properties.$set` overwrites person properties; `$set_once` fills only
   keys that are absent. Within one event `$set` applies first.

Because identity depends on order, resolution reads consult the EF change
tracker (`.Local`) before the database — events earlier in the same batch
(not yet flushed) are visible to later ones — and the queue guarantees
cross-batch ordering.

## Query engine

`QueryService` splits work between SQL and memory:

- **In SQL:** project + event-name + time-range filtering. This is why the
  timestamp column is stored as UTC ticks (see below) and covered by
  `(ProjectId, Name, Timestamp)` / `(ProjectId, PersonId, Timestamp)`
  indexes.
- **In memory, on the filtered slice:** property/person/cohort filter
  evaluation (properties are opaque JSON to SQLite), interval bucketing with
  zero-fill (`TimeBucket`: hour/day/week, weeks start Monday, all UTC),
  breakdown grouping (top-N by count with ordinal tie-break, `(none)` for
  missing values, `(other)` aggregating the overflow), funnel traversal, and
  the retention triangle.

Funnels walk each person's events in timestamp order (ties resolve by step
index, so simultaneous events count as ordered progression) and count the
deepest step reached inside a conversion window anchored at the person's
earliest step-1 event. Retention buckets by UTC day with an
inclusive-start/exclusive-end range; cohort entry is the first qualifying
event, and later cohorts have triangularly fewer observable days.

Saved insights store their query config as JSON; `InsightRunnerService`
replays that config through the same `QueryService` for dashboard refresh and
insight exports, degrading to a per-insight error instead of failing a whole
dashboard.

Cohorts resolve at query time: static cohorts read membership rows; dynamic
cohorts re-evaluate their rules (person-property filters and "performed X in
the last N days") against live data on every use — there is no materialized
membership to go stale.

## Feature flags

`FeatureFlagHasher.Rank` hashes `"{flagKey}.{distinctId}"` with SHA-256 and
maps the first 8 bytes onto `[0, 1]`. A flag at N% is on when
`rank * 100 < N` — deterministic per (flag, user), no storage, monotonic as
rollouts increase. Variant selection re-ranks with a `.variant` salt so the
A/B split is independent of the rollout gate; variant weights must sum
to 100. Targeting filters (person properties, cohort membership) gate
eligibility before the rollout hash and fail closed when a referenced cohort
does not exist. `/decide` evaluates every flag server-side;
`/feature-flags/local-evaluation` hands SDKs the full definitions instead.

## The SQLite `DateTimeOffset` converter

SQLite has no native date-time type; EF's default maps `DateTimeOffset` to
TEXT, which cannot be compared or ordered correctly in SQL — exactly what a
time-series query engine does constantly. `PulseDbContext` therefore installs
a model-wide converter storing every `DateTimeOffset` as its **UTC ticks**
(`long`), so range filters and `ORDER BY` translate to integer comparisons
and the timestamp indexes work. The trade-off: the original client offset is
normalized to `+00:00` on read, which is correct for analytics where
everything is compared in UTC. Cursors embed the same ticks value, keeping
cursor comparisons consistent with column comparisons.

## Cross-cutting

- **Auth:** a policy scheme dispatches `Authorization: Bearer …` on prefix —
  `pk_user_` keys go to a custom handler (SHA-256 hash lookup), everything
  else to JWT bearer validation. Project-scoped authorization lives in
  `ProjectAccessService`: membership for management, membership *or* the
  project read key (`X-Api-Key`) for query endpoints, and non-members always
  get 404 so project ids don't leak.
- **Errors:** every failure path returns RFC 7807 problem details, including
  malformed request bodies (mapped from `BadHttpRequestException` to 400) and
  unhandled exceptions.
- **Rate limiting:** `/capture` uses a fixed window partitioned by write key
  (falling back to client IP), configurable via
  `RateLimiting:Capture:PermitLimit` / `WindowSeconds`, no queueing — SDKs
  back off on 429 and retry.
- **Schema:** `EnsureCreated()` on startup, no migrations — simple by design
  for a SQLite-backed reference implementation.
