# pulse-net

A PostHog/Mixpanel-style product-analytics platform — C#/.NET backend only.

Projects own write keys; SDKs push events to `POST /capture` (async ingestion
with a durable queue and dead-letter storage); the platform resolves anonymous
device ids into persons (with `$identify` merging and `$set`/`$set_once`
person properties); and the query engine answers trends (with breakdowns and
annotations), funnels and retention questions over the event stream — all
filterable by event/person properties and cohorts. On top of that: user
accounts with JWT + personal API keys, feature flags with deterministic
rollout and a `/decide` endpoint, dashboards with one-shot refresh, an
auto-populated event/property registry, GDPR person deletion, CSV/JSON
exports (sync + async jobs), rate limiting and health probes.

## Stack

- .NET 10 / ASP.NET Core minimal APIs
- EF Core + SQLite (`DateTimeOffset` stored as UTC ticks so SQL can order and
  range-filter timestamps — SQLite cannot compare `DateTimeOffset` natively)
- JWT bearer auth (HS256) + PBKDF2 password hashing + hashed personal API keys
- Channels-signalled background workers for ingestion and export jobs
- xUnit: unit tests + `WebApplicationFactory` integration tests over
  shared-cache in-memory SQLite (so the background workers run in tests)

## Layout

| Project | Purpose |
| --- | --- |
| `src/Pulse.Domain` | Entities and pure domain logic (property filters, cohort rules, flag hashing, time bucketing, CSV), no dependencies |
| `src/Pulse.Infrastructure` | EF Core `DbContext`, capture/identity/query/cohort/flag/export services, ingestion + export processors, demo seeder |
| `src/Pulse.Api` | HTTP endpoints, contracts, auth, background workers, validation |
| `tests/Pulse.Tests` | Unit + integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Pulse.Api            # serves the API (SQLite: pulse.db)
dotnet run --project src/Pulse.Api -- seed    # demo project + 30 days of traffic; prints keys + a demo login
```

## Authentication model

| Credential | Prefix | Grants |
| --- | --- | --- |
| JWT session (`POST /api/auth/login`) | — | Full management API, scoped by project membership |
| Personal API key | `pk_user_` | Same as a JWT, for scripts/CI (`Authorization: Bearer pk_user_…`); stored hashed, shown once |
| Project write key | `pk_live_` | `POST /capture` and `POST /decide` only |
| Project read key | `rk_live_` | Query endpoints + flag local-evaluation payload via `X-Api-Key` |

Non-members receive `404` (not `403`) for projects they cannot see. All error
paths return RFC 7807 problem details.

## API

### Auth & accounts

| Route | Description |
| --- | --- |
| `POST /api/auth/register` `{ email, password, name }` | Create an account; returns a JWT |
| `POST /api/auth/login` `{ email, password }` | Sign in; returns a JWT |
| `GET /api/auth/me` | Current user |
| `POST /api/personal-api-keys` `{ name }` | Create a personal key (plaintext returned once) |
| `GET /api/personal-api-keys` / `DELETE …/{id}` | List (masked) / revoke keys |

### Projects & membership

| Route | Description |
| --- | --- |
| `POST /api/projects` `{ name }` | Create a project (creator becomes a member); returns `pk_live_` write + `rk_live_` read keys |
| `GET /api/projects` | List projects you belong to |
| `GET /api/projects/{id}` | Get one project |
| `POST /api/projects/{id}/members` `{ email }` / `GET …/members` | Invite an existing user / list members |

### Ingestion

`POST /capture` — authenticated by the write key (`api_key` in the body or
`X-Api-Key` header). Accepts a single event or a batch (max 1000). Valid
payloads are appended to a durable queue and the endpoint returns **202**;
a background worker validates and persists them in enqueue order, moving
poison events to dead-letter storage. Rate limited per write key.

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

| Route | Description |
| --- | --- |
| `GET /api/ingestion/metrics` | Queue depth, dead letters, lifetime processed counters (no auth, health-style) |
| `GET /api/projects/{id}/ingestion/dead-letters` | Inspect poison events (member-only) |

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
| `DELETE /api/projects/{id}/persons/{personId}` | GDPR purge: person + events + distinct ids + cohort rows; returns a deletion receipt |

### Analytics & insights

| Route | Description |
| --- | --- |
| `GET …/insights/trend?event=&from=&to=&interval=hour\|day\|week&filters=&breakdown=&breakdownLimit=` | Counts + unique persons per zero-filled bucket; optional property breakdown (top-N + `(other)`); annotations in range included |
| `POST …/insights/funnel` `{ steps, from, to, windowDays, filters }` | Ordered per-person step conversion within a window |
| `GET …/insights/retention?from=&days=&targetEvent=&filters=` | Day-N cohort retention triangle |
| `POST /api/projects/{id}/insights` `{ name, type, config }` | Save an insight definition (config = the query's parameters) |
| `GET …/insights` / `…/insights/{insightId}` | List (paginated) / fetch saved insights |

`filters` is a JSON array combining with AND:
`[{"property":"url","operator":"equals|contains|is_set|is_not_set","value":"/pricing","type":"event|person"},{"type":"cohort","value":"<cohortId>"}]`.
Query endpoints also accept the project read key via `X-Api-Key`.

### Cohorts

| Route | Description |
| --- | --- |
| `POST /api/projects/{id}/cohorts` | `{ name, type: "static", personIds: [...] }` or `{ name, type: "dynamic", rules: [...] }` |
| `GET …/cohorts` / `…/{cohortId}` / `DELETE …/{cohortId}` | List (paginated) / fetch / delete |
| `GET …/cohorts/{cohortId}/persons` | Current members (computed for dynamic cohorts) |
| `POST …/cohorts/{cohortId}/persons` / `DELETE …/persons/{personId}` | Edit static-cohort members |

Dynamic rules (AND):
`{"kind":"property","property":"plan","operator":"equals","value":"pro"}` and
`{"kind":"performed_event","event":"purchase","days":30,"minCount":2}`.

### Feature flags

| Route | Description |
| --- | --- |
| `POST /decide` `{ api_key: "pk_live_…", distinct_id }` | Evaluate all flags for a user: `{ featureFlags: { key: true\|false\|"variant" } }` |
| `POST /api/projects/{id}/feature-flags` | Create: `{ key, type: "boolean"\|"multivariate", rolloutPercentage, filters, variants }` |
| `GET …/feature-flags` / `…/{key}` / `PUT …/{key}` / `DELETE …/{key}` | CRUD (list paginated) |
| `GET …/feature-flags/local-evaluation` | Full definitions for SDK-side evaluation (read-key accessible) |

Rollout is a deterministic SHA-256 hash on `flagKey.distinct_id` — the same
user always gets the same answer. Multivariate variants must sum to 100 and
are picked with an independent salt. Targeting filters accept person
properties and cohorts.

### Dashboards

| Route | Description |
| --- | --- |
| `POST /api/projects/{id}/dashboards` `{ name, description }` | Create a dashboard |
| `GET …/dashboards` / `…/{dashboardId}` / `PUT` / `DELETE` | CRUD; list (paginated) includes tile counts |
| `POST …/{dashboardId}/tiles` `{ insightId, layout }` | Add a saved insight as a tile (layout is opaque JSON) |
| `PUT …/tiles/{tileId}` / `DELETE …/tiles/{tileId}` | Move / remove tiles |
| `POST …/{dashboardId}/refresh` | Run every tile's query in one response (per-tile errors don't fail the refresh) |

### Annotations & data management

| Route | Description |
| --- | --- |
| `POST /api/projects/{id}/annotations` `{ date, content }` | Dated note; appears in trend responses covering the date |
| `GET …/annotations?from=&to=` / `PUT …/{id}` / `DELETE …/{id}` | CRUD with date-range filtering (paginated) |
| `GET /api/projects/{id}/event-definitions` | Event names seen in the project (auto-populated on ingest, paginated) |
| `GET /api/projects/{id}/property-definitions` | Property keys + first-observed JSON type (system `$…` keys excluded) |

### Export

| Route | Description |
| --- | --- |
| `GET /api/projects/{id}/export/events?format=csv\|json&event=&from=&to=&filters=&cursor=&limit=` | Cursor-paginated event export (`nextCursor` / `X-Next-Cursor`) |
| `GET /api/projects/{id}/export/persons?format=&cursor=&limit=` | Cursor-paginated person export |
| `GET /api/projects/{id}/export/insights/{insightId}?format=` | Run a saved insight and export the result |
| `POST /api/projects/{id}/exports` `{ type: "events"\|"persons"\|"insight", format, … }` | Create an async export job (202) |
| `GET …/exports/{jobId}` / `…/exports/{jobId}/download` | Poll status / download the finished document |

### Operations

| Route | Description |
| --- | --- |
| `GET /health` | Liveness + database and ingestion-queue probes (`degraded` on deep backlog, 503 on DB failure) |
| `GET /api/ingestion/metrics` | Ingestion throughput and queue depth |

Every request is logged (`method path → status in ms`); `/capture` is rate
limited per write key (fixed window, `RateLimiting:Capture:PermitLimit`,
default 300/min).

## Development notes

- The schema is created with `EnsureCreated()` on startup — simple by design
  for a SQLite-backed reference implementation. Delete `pulse.db` after
  pulling schema changes.
- Connection string override: `ConnectionStrings__Pulse` (defaults to
  `Data Source=pulse.db`). JWT settings live under `Jwt:` in appsettings.
- Query engine strategy: time-range and event-name filtering run in SQL
  against the ticks-indexed timestamp column; property/cohort filtering,
  interval bucketing, breakdowns, funnel traversal and cohort math run in
  memory on the filtered slice.
- All list endpoints accept `limit` (≤ 500) and `offset`; exports use opaque
  cursors instead so pages stay stable under concurrent writes.
