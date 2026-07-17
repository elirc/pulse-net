# pulse-net

A PostHog/Mixpanel-style product-analytics platform — C#/.NET backend only.

Projects own write keys; SDKs push events to `POST /capture`; the platform
resolves anonymous device ids into persons (with `$identify` merging and
`$set`/`$set_once` person properties); and the query engine answers trends,
funnels and retention questions over the event stream.

## Stack

- .NET 10 / ASP.NET Core minimal APIs
- EF Core + SQLite (`DateTimeOffset` stored as UTC ticks so SQL can order and
  range-filter timestamps — SQLite cannot compare `DateTimeOffset` natively)
- xUnit: unit tests + `WebApplicationFactory` integration tests over in-memory SQLite

## Layout

| Project | Purpose |
| --- | --- |
| `src/Pulse.Domain` | Entities and pure domain logic (property merging, time bucketing), no dependencies |
| `src/Pulse.Infrastructure` | EF Core `DbContext`, capture/identity/query services, demo seeder |
| `src/Pulse.Api` | HTTP endpoints, contracts, validation |
| `tests/Pulse.Tests` | Unit + integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Pulse.Api            # serves the API (SQLite: pulse.db)
dotnet run --project src/Pulse.Api -- seed    # generates a demo project + 30 days of traffic, prints its API key
```

## API

### Projects

| Route | Description |
| --- | --- |
| `POST /api/projects` `{ "name": "My App" }` | Create a project; the response contains its generated `pk_live_…` write key |
| `GET /api/projects` | List projects |
| `GET /api/projects/{id}` | Get one project |

### Ingestion

`POST /capture` — authenticated by `api_key` in the body or an `X-Api-Key`
header. Accepts a single event or a batch (max 1000). The whole payload is
written atomically; timestamps default to server time.

```jsonc
// single
{ "api_key": "pk_live_…", "event": "pageview", "distinct_id": "device-1",
  "timestamp": "2026-03-01T10:00:00Z", "properties": { "url": "/pricing" } }

// batch
{ "api_key": "pk_live_…", "batch": [
  { "event": "signup", "distinct_id": "device-1",
    "properties": { "$set": { "email": "ada@example.com" },
                     "$set_once": { "initial_referrer": "google" } } },
  { "event": "$identify", "distinct_id": "user-ada",
    "properties": { "$anon_distinct_id": "device-1" } }
] }
```

Identity rules (PostHog semantics):

- Every unseen `distinct_id` creates a person; further events reuse it.
- `$identify` with `$anon_distinct_id` aliases both ids onto one person. If
  both ids already own separate persons they are merged — distinct ids and
  historical events repointed, properties merged (identified person wins) —
  and repeated identifies are idempotent.
- `properties.$set` overwrites person properties; `$set_once` only fills
  missing keys. Both work on any event.

### Persons

| Route | Description |
| --- | --- |
| `GET /api/projects/{id}/persons?limit=&offset=` | Page through persons (distinct ids + JSON properties) |
| `GET /api/projects/{id}/persons/{personId}` | Get one person |
| `GET /api/projects/{id}/persons/by-distinct-id/{distinctId}` | Resolve a distinct id |

### Analytics

| Route | Description |
| --- | --- |
| `GET /api/projects/{id}/insights/trend?event=&from=&to=&interval=hour\|day\|week` | Event counts + unique persons per zero-filled bucket (UTC, Monday weeks) |
| `POST /api/projects/{id}/insights/funnel` `{ "steps": ["signup","activate","purchase"], "from": …, "to": …, "windowDays": 14 }` | Ordered per-person step conversion with a conversion window; returns persons per step and conversion ratios |
| `GET /api/projects/{id}/insights/retention?from=2026-03-01&days=7&targetEvent=signup` | Day-N cohort retention triangle; cohort entry optionally gated on a target event |
| `POST /api/projects/{id}/insights` `{ "name": …, "type": "trend\|funnel\|retention", "config": { … } }` | Save an insight definition |
| `GET /api/projects/{id}/insights` / `…/{insightId}` | List / fetch saved insights |

All error paths return RFC 7807 problem details: `401` for missing/unknown API
keys, `400` with field-level errors for validation failures, `404` for unknown
projects/persons/insights.

## Development notes

- The schema is created with `EnsureCreated()` on startup — simple by design
  for a SQLite-backed reference implementation.
- Connection string override: `ConnectionStrings__Pulse` (defaults to
  `Data Source=pulse.db`).
- Query engine strategy: time-range filtering and event-name filtering run in
  SQL against the ticks-indexed timestamp column; interval bucketing, funnel
  traversal and cohort math run in memory on the filtered slice.
