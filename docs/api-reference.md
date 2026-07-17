# API reference

Every endpoint, with its auth requirement, request/response shape and error
codes. All errors are RFC 7807 problem details (`application/problem+json`);
validation failures are 400s with per-field `errors`.

## Auth vocabulary

| Auth column | Meaning |
| --- | --- |
| — | No authentication |
| **member** | JWT or `pk_user_` personal key (`Authorization: Bearer …`) belonging to a member of the project. Non-members get **404**, unauthenticated callers **401**. |
| **member / read key** | Same as member, *or* the project's `rk_live_` read key in the `X-Api-Key` header. A write key in `X-Api-Key` is rejected with 401. |
| **write key** | The project's `pk_live_` key, as `api_key` in the body or the `X-Api-Key` header. Unknown/missing key → 401. |
| **user** | Any authenticated user (JWT or personal key), no project scope. |

All list endpoints take `limit` (1–500, default 100) and `offset` (≥ 0)
query parameters and return plain JSON arrays unless noted. Timestamps are
ISO-8601 and normalized to UTC.

### Filters

Query endpoints accept `filters`, a JSON array (AND semantics) of:

```jsonc
{ "property": "url", "operator": "equals", "value": "/pricing", "type": "event" }
// operator: equals | contains | is_set | is_not_set
// type:     event (default) | person
{ "type": "cohort", "value": "<cohort id GUID>" }
```

On GET endpoints the array is passed URL-encoded in the `filters` query
parameter; on POST endpoints it is a body field. Invalid filter JSON → 400.

---

## Auth & accounts

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/auth/register` | — | `{ email, password, name }` | **201** `{ token, expiresAt, user: { id, email, name, createdAt } }` |
| `POST /api/auth/login` | — | `{ email, password }` | **200** same shape as register |
| `GET /api/auth/me` | user | — | **200** `{ id, email, name, createdAt }` |
| `POST /api/personal-api-keys` | user | `{ name }` | **201** `{ id, name, key, createdAt }` — `key` (`pk_user_…`) is shown **once**; only its SHA-256 hash is stored |
| `GET /api/personal-api-keys` | user | — | **200** `[{ id, name, keySuffix, createdAt }]` (masked) |
| `DELETE /api/personal-api-keys/{keyId}` | user | — | **204**; the key stops authenticating immediately |

Errors: register 400 (invalid email, password < 8 chars, missing name),
409 (email already registered); login 401 (invalid credentials);
key create 400 (missing name); key delete 404 (not yours / unknown).

## Projects & membership

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects` | user | `{ name }` | **201** `{ id, name, apiKey, readKey, createdAt }` — creator becomes a member; `apiKey` = `pk_live_…` write key, `readKey` = `rk_live_…` |
| `GET /api/projects` | user | — | **200** projects the caller belongs to |
| `GET /api/projects/{id}` | member | — | **200** project |
| `POST /api/projects/{id}/members` | member | `{ email }` | **201** `{ userId, email, name, addedAt }`; idempotent for existing members |
| `GET /api/projects/{id}/members` | member | — | **200** `[{ userId, email, name, addedAt }]` |

Errors: create 400 (blank name); add member 400 (missing email), 404
(no account with that email).

## Ingestion

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /capture` | write key | single event or `{ batch: [...] }` (see below) | **202** `{ status: "queued", queued: <n> }` |
| `GET /api/ingestion/metrics` | — | — | **200** `{ pending, deadLetters, processedTotal, deadLetteredTotal }` |
| `GET /api/projects/{id}/ingestion/dead-letters?limit=` | member | — | **200** `[{ id, payloadJson, error, attempts, failedAt }]`, newest first |

Capture payloads (`event` and `distinct_id` required; `timestamp` defaults to
server time; `properties` must be a JSON object — other shapes are coerced to
`{}`):

```jsonc
// single
{ "api_key": "pk_live_…", "event": "pageview", "distinct_id": "device-1",
  "timestamp": "2026-03-01T10:00:00Z", "properties": { "url": "/pricing" } }

// batch (1–1000 events; one invalid item rejects the whole batch)
{ "api_key": "pk_live_…", "batch": [
  { "event": "signup", "distinct_id": "device-1",
    "properties": { "$set": { "email": "ada@example.com" },
                     "$set_once": { "initial_referrer": "google" } } },
  { "event": "$identify", "distinct_id": "user-ada",
    "properties": { "$anon_distinct_id": "device-1" } }
] }
```

Errors: 401 (missing/unknown key), 400 (empty batch, batch > 1000, missing
`event`/`distinct_id` with per-item field errors like `batch[1].event`),
429 (rate limited — fixed window per write key, defaults 300/60 s).

Persistence is asynchronous: a 202 means *queued*, and the background worker
persists in enqueue order. Events that fail during processing land in
dead-letter storage (permanent failures immediately; transient failures after
3 attempts).

## Persons

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `GET /api/projects/{id}/persons` | member | — | **200** `[{ id, projectId, properties, distinctIds, createdAt }]` |
| `GET /api/projects/{id}/persons/{personId}` | member | — | **200** person / **404** |
| `GET /api/projects/{id}/persons/by-distinct-id/{distinctId}` | member | — | **200** person / **404** |
| `DELETE /api/projects/{id}/persons/{personId}` | member | — | **200** `{ personId, deletedEvents, deletedDistinctIds }` — GDPR purge: person + events + distinct-id mappings + cohort rows, one transaction / **404** |

## Insights & queries

All three query endpoints accept **member / read key** auth and the
[filters](#filters) format.

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `GET /api/projects/{id}/insights/trend` | member / read key | query: `event` (required), `from`, `to` (default: last 30 days), `interval` = `hour\|day\|week` (default `day`), `filters`, `breakdown`, `breakdownLimit` (1–25, default 5) | **200** without breakdown: `{ event, interval, from, to, buckets: [{ start, count, uniquePersons }], annotations: [{ id, date, content }] }`; with breakdown: `{ …, breakdown, series: [{ value, total, buckets }] }` — top-N by count plus `(other)`, missing property → `(none)` |
| `POST /api/projects/{id}/insights/funnel` | member / read key | `{ steps: ["signup","activate",…] (≥ 2), from, to, windowDays (1–90, default 14), filters }` | **200** `{ from, to, windowDays, steps: [{ order, event, persons, conversionFromPrevious, conversionFromFirst }] }` |
| `GET /api/projects/{id}/insights/retention` | member / read key | query: `from` (date, default: window ending today), `days` (1–60, default 7), `targetEvent`, `filters` | **200** `{ from, days, targetEvent, cohorts: [{ cohortDate, size, returnedByDay: [d0, d1, …] }] }` (triangular horizons) |
| `POST /api/projects/{id}/insights` | member | `{ name, type: "trend"\|"funnel"\|"retention", config }` — `config` holds the same parameters the ad-hoc endpoint takes | **201** `{ id, projectId, name, type, config, createdAt }` |
| `GET /api/projects/{id}/insights` | member | — | **200** saved insights, oldest first |
| `GET /api/projects/{id}/insights/{insightId}` | member | — | **200** insight / **404** |

Errors: 400 for missing `event`, bad `interval`, `from > to`, < 2 funnel
steps, out-of-range `windowDays`/`days`/`breakdownLimit`, invalid filters or
insight type.

Buckets are zero-filled across the whole range, in UTC (weeks start Monday).
Funnels count each person's deepest step reached in order, with the
conversion window anchored at their earliest first-step event; timestamp ties
resolve by step order.

## Cohorts

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects/{id}/cohorts` | member | `{ name, type: "static", personIds: [...] }` or `{ name, type: "dynamic", rules: [...] }` | **201** `{ id, projectId, name, type, rules, createdAt }` |
| `GET /api/projects/{id}/cohorts` | member | — | **200** list |
| `GET /api/projects/{id}/cohorts/{cohortId}` | member | — | **200** / **404** |
| `DELETE /api/projects/{id}/cohorts/{cohortId}` | member | — | **204** (member rows removed too) |
| `GET /api/projects/{id}/cohorts/{cohortId}/persons` | member | — | **200** `{ cohortId, count, personIds }` — computed live for dynamic cohorts |
| `POST /api/projects/{id}/cohorts/{cohortId}/persons` | member | `{ personIds: [...] }` | **200** `{ added, total }` — static cohorts only; unknown/cross-project ids are ignored |
| `DELETE /api/projects/{id}/cohorts/{cohortId}/persons/{personId}` | member | — | **204** |

Dynamic rules (AND, evaluated live at query time):

```jsonc
{ "kind": "property", "property": "plan", "operator": "equals", "value": "pro" }
{ "kind": "performed_event", "event": "purchase", "days": 30, "minCount": 2 }
// days 1–365 (default 30), minCount ≥ 1 (default 1)
```

Errors: 400 (blank name, bad type, empty/invalid `rules` for dynamic,
editing members of a dynamic cohort).

## Feature flags

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /decide` | write key | `{ api_key, distinct_id }` | **200** `{ featureFlags: { "<key>": true \| false \| "<variant>" } }` — every flag in the project |
| `POST /api/projects/{id}/feature-flags` | member | `{ key, name?, type: "boolean"\|"multivariate", active? (default true), rolloutPercentage? (0–100, default 100), filters?, variants? }` | **201** flag |
| `GET /api/projects/{id}/feature-flags` | member | — | **200** list, ordered by key |
| `GET /api/projects/{id}/feature-flags/local-evaluation` | member / read key | — | **200** `{ flags: [<full definitions>] }` for SDK-side evaluation |
| `GET /api/projects/{id}/feature-flags/{key}` | member | — | **200** / **404** |
| `PUT /api/projects/{id}/feature-flags/{key}` | member | any subset of `{ name, active, rolloutPercentage, filters, variants }` | **200** updated flag |
| `DELETE /api/projects/{id}/feature-flags/{key}` | member | — | **204** / **404** |

Flag response shape: `{ id, projectId, key, name, type, active,
rolloutPercentage, filters, variants, createdAt }`.

- `key`: letters, digits, `-`, `_` only; unique per project (**409** on
  duplicates).
- `filters`: person/cohort targeting only (no `event` type) — gate
  eligibility before the rollout hash; a missing/deleted cohort fails closed.
- `variants` (multivariate only): `[{ "key": "control", "rolloutPercentage": 50 }, …]`
  summing to 100; boolean flags must not have variants.
- Evaluation is deterministic: SHA-256 of `flagKey.distinct_id` buckets the
  user; raising the rollout never drops users who already had the flag.

Errors: `/decide` 401 (bad key), 400 (missing `distinct_id`); CRUD 400
(invalid key/type/rollout/filters/variants), 409 (duplicate key).

## Dashboards

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects/{id}/dashboards` | member | `{ name, description? }` | **201** `{ id, projectId, name, description, tiles: [], createdAt }` |
| `GET /api/projects/{id}/dashboards` | member | — | **200** `[{ id, projectId, name, description, tileCount, createdAt }]` |
| `GET /api/projects/{id}/dashboards/{dashboardId}` | member | — | **200** dashboard with `tiles: [{ id, insightId, insightName, insightType, layout, createdAt }]` / **404** |
| `PUT /api/projects/{id}/dashboards/{dashboardId}` | member | `{ name?, description? }` | **200** updated dashboard |
| `DELETE /api/projects/{id}/dashboards/{dashboardId}` | member | — | **204** (tiles removed too) |
| `POST /api/projects/{id}/dashboards/{dashboardId}/tiles` | member | `{ insightId, layout? }` — layout is opaque JSON | **201** tile |
| `PUT …/tiles/{tileId}` | member | `{ layout }` | **200** tile |
| `DELETE …/tiles/{tileId}` | member | — | **204** |
| `POST /api/projects/{id}/dashboards/{dashboardId}/refresh` | member | — | **200** `{ dashboardId, name, refreshedAt, tiles: [{ tileId, insightId, insightName, insightType, layout, result, error }] }` — every tile's query runs; a failing tile reports `error` without failing the refresh |

Errors: 400 (blank name, missing `insightId`, insight not in this project).

## Annotations & data management

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects/{id}/annotations` | member | `{ date: "YYYY-MM-DD", content }` | **201** `{ id, projectId, date, content, createdAt }` |
| `GET /api/projects/{id}/annotations?from=&to=` | member | — | **200** list, by date; annotations also ride along in trend responses covering their date |
| `PUT /api/projects/{id}/annotations/{annotationId}` | member | `{ date?, content? }` | **200** / **404** |
| `DELETE /api/projects/{id}/annotations/{annotationId}` | member | — | **204** / **404** |
| `GET /api/projects/{id}/event-definitions` | member | — | **200** `[{ name, firstSeenAt, lastSeenAt }]` — auto-populated on ingest |
| `GET /api/projects/{id}/property-definitions` | member | — | **200** `[{ name, propertyType, firstSeenAt, lastSeenAt }]` — `propertyType` is the first-observed JSON kind; system `$…` keys excluded |

## Export

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `GET /api/projects/{id}/export/events` | member | query: `format=csv\|json` (default json), `event`, `from`, `to`, `filters`, `cursor`, `limit` (1–1000, default 100) | **200** JSON `{ events: [{ id, timestamp, event, distinctId, personId, properties }], nextCursor }` or CSV; `nextCursor` also in the `X-Next-Cursor` header |
| `GET /api/projects/{id}/export/persons` | member | query: `format`, `cursor`, `limit` | **200** `{ persons: [{ id, createdAt, distinctIds, properties }], nextCursor }` or CSV |
| `GET /api/projects/{id}/export/insights/{insightId}` | member | query: `format` | **200** the insight's query result as JSON or CSV / **404**; 400 if the stored config fails to run |
| `POST /api/projects/{id}/exports` | member | `{ type: "events"\|"persons"\|"insight", format: "csv"\|"json", event?, from?, to?, filters?, insightId? }` | **202** job `{ id, projectId, type, format, status, rowCount, error, createdAt, completedAt }` + `Location` |
| `GET /api/projects/{id}/exports/{jobId}` | member | — | **200** job; `status`: `pending → running → completed \| failed` |
| `GET /api/projects/{id}/exports/{jobId}/download` | member | — | **200** the document (`text/csv` or `application/json`) / **409** if not `completed` / **404** |

Cursors are opaque base64 of `(timestamp ticks, id)`; pages are ordered by
`(timestamp, id)` so they stay stable under concurrent appends — no skips, no
duplicates. Property filters apply after the SQL page scan, so a filtered
page may hold fewer than `limit` rows while the cursor still advances. Async
exports are capped at 50,000 rows. Errors: 400 (bad `format`, invalid
`cursor`, bad job `type`, insight export without `insightId`, invalid
filters).

## Operations

| Method & route | Auth | Response |
| --- | --- | --- |
| `GET /health` | — | **200** `{ status: "healthy"\|"degraded", service, timestamp, checks: { database, queue: { pending, deadLetters } } }`; **503** with `status: "unhealthy"` when the database fails. `degraded` = ingestion backlog > 10,000 rows |
| `GET /api/ingestion/metrics` | — | **200** queue depth + lifetime counters |

Every request except `/health` emits one structured log line
(`HTTP {method} {path} responded {status} in {ms}`). `/capture` is rate
limited per write key (falling back to client IP) with a fixed window:
`RateLimiting:Capture:PermitLimit` (default 300) per
`RateLimiting:Capture:WindowSeconds` (default 60); rejected requests get
**429**.
