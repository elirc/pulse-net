# API reference

Current bootcamp additions include [recoverable exports and snapshots](runbooks/exports.md),
[versioned event retention](runbooks/retention.md), [restricted personal tokens](runbooks/personal-tokens.md),
[person erasure](runbooks/person-erasure.md), and [ingestion tracing](runbooks/ingestion-tracing.md). Their implementation and
execution evidence is tracked in [the verification record](../astradocs/bootcamp/verification.md).

Every endpoint, with its auth requirement, request/response shape and error
codes. All errors are RFC 7807 problem details (`application/problem+json`);
validation failures are 400s with per-field `errors`.

## Auth vocabulary

Capture responses include `X-Trace-Id`, including rejected requests that reach the
application middleware. It correlates admission and separate worker-attempt spans;
replay uses its own request trace with an original-context link. This diagnostic
identifier is not a credential, receipt, or deduplication key. No telemetry collector
is required; see [tracing operations](runbooks/ingestion-tracing.md).

| Auth column | Meaning |
| --- | --- |
| — | No authentication |
| **member** | JWT or `pk_user_` personal key (`Authorization: Bearer …`) belonging to a member of the project. Non-members get **404**, unauthenticated callers **401**. |
| **member / read key** | Same as member, *or* the project's `rk_live_` read key in the `X-Api-Key` header. A write key in `X-Api-Key` is rejected with 401. |
| **write key** | The project's `pk_live_` key, as `api_key` in the body or the `X-Api-Key` header. Unknown/missing key → 401. |
| **user** | Any authenticated user (JWT or personal key), no project scope. |
| **viewer / editor / admin** | Minimum current project role; higher roles include lower roles. JWT/personal-key authentication is still required. |

Project membership also has a current role: viewer, editor, or admin. The
[permission matrix](project-permissions.md) specifies the minimum role for every
project route, including routes whose Auth column below says `member`. A visible
member below that minimum gets 403. Read-only previews/refreshes allow viewers;
configuration writes require editor; access management requires admin. Roles
are read from the database per request, including personal-key requests.

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
| `POST /api/personal-api-keys` | JWT session | `{ name }` | **201** legacy unrestricted key; `key` (`pk_user_…`) is shown once, only SHA-256 hash stored |
| `POST /api/personal-api-keys/restricted` | JWT session | `{name,projectIds,scopes,expiresAt}` | **201** `{id,name,key,createdAt,mode,expiresAt,projectIds,scopes}`; plaintext once, no-store |
| `GET /api/personal-api-keys` | JWT session | — | **200** `[{id,name,keySuffix,createdAt,mode,expiresAt}]` (masked) |
| `DELETE /api/personal-api-keys/{keyId}` | JWT session | — | **204**; the key stops authenticating immediately |

Errors: register 400 (invalid email, password < 8 chars, missing name),
409 (email already registered); login 401 (invalid credentials);
key create 400 (missing name); key delete 404 (not yours / unknown).

Personal-key management requires an actual JWT session; neither a legacy nor a
restricted personal token can mint another token. Restricted creation requires
1–20 project IDs belonging to the caller, 1–4 named scopes, a trimmed name of
1–200 characters, and expiry 1 minute through 90 days ahead, inclusive. Named
scopes are `analytics:read`, `configuration:read`, `configuration:write`, and
`exports:write`. Allowed projects, current membership/role, required route scope,
and unexpired credential must all agree. Scope grants do not imply one another.
Restricted responses omit project credentials even for Admin users. Existing
keys migrate to explicit `legacyUnrestricted` mode. See the [token runbook](runbooks/personal-tokens.md).

## Projects & membership

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects` | user | `{ name }` | **201** `{ id, name, role:"admin", apiKey, readKey, createdAt }` — creator becomes admin; `apiKey` = `pk_live_…` write key, `readKey` = `rk_live_…` |
| `GET /api/projects` | user | — | **200** projects the caller belongs to |
| `GET /api/projects/{id}` | member | — | **200** project |
| `PUT /api/projects/{id}` | admin | `{ name }` (trimmed, 1–200 characters) | **200** project; only its name changes |
| `POST /api/projects/{id}/members` | admin | `{ email }` | **201** `{ userId, email, name, addedAt, role }`; new members are viewers; existing membership is unchanged |
| `GET /api/projects/{id}/members` | viewer | — | **200** `[{ userId, email, name, addedAt, role }]` |
| `PUT /api/projects/{id}/members/{userId}/role` | admin | `{ role:"viewer"\|"editor"\|"admin" }` | **200** member; invalid named role **400**, missing membership **404**, last-admin demotion **409** |
| `DELETE /api/projects/{id}/members/{userId}` | admin | — | **204**; missing membership **404**, last-admin removal **409** |

Project list/detail responses include the caller's role. `apiKey` and `readKey`
are absent from viewer/editor JSON, not merely masked. Existing memberships
upgrade to admin to preserve their previous management privileges. Member
mutations serialize and recheck authority inside their transaction. Removing
a membership makes the next project request return 404.

Project listing accepts `nameContains` (trimmed, max 200, literal case-sensitive
substring) and `sort=oldest|newest` (case-insensitive; default oldest). Sorting
uses creation time then ID, and filtering precedes pagination. Member listing
accepts `emailContains` (trimmed/lowercased, max 320). Blank optional filters
are omitted. Invalid sort/filter values return 400.

Errors: create/rename 400 (blank name or more than 200 trimmed characters); add member 400 (missing email), 404
(no account with that email).

## Ingestion

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /capture` | write key | single event or `{ batch: [...] }` (see below) | **202** `{ status: "queued", queued: <n> }` |
| `GET /api/ingestion/metrics` | — | — | **200** `{ pending, deadLetters, processedTotal, deadLetteredTotal }` |
| `GET /api/projects/{id}/ingestion/dead-letters?limit=` | member | — | **200** `[{ id, payloadJson, error, attempts, failedAt }]`, newest first |
| `GET /api/projects/{id}/ingestion/dead-letters/{letterId}` | member | — | **200** same letter fields / **404**; inspection has no replay effect |
| `GET /api/projects/{id}/ingestion/metrics` | member | — | **200** `{ pending, deadLetters, oldestEnqueuedAt, oldestPendingAgeSeconds, delayed, nextAttemptAt }` |
| `POST /api/projects/{id}/ingestion/dead-letters/{letterId}/replay` | member | No body | **202** `{ status: "queued", queued: 1 }` / **404** missing or already consumed letter / **422** invalid stored payload |

Project metrics report database-backed counts for that project. The oldest
timestamp and age are null when its queue is empty; age is nonnegative seconds
from enqueue time to the current UTC clock. The queue aggregation and dead-letter
count are separate observations and may change during concurrent processing.

Dead-letter listing accepts `offset` (negative values become zero) and
`errorContains` (trimmed, max 100, literal case-sensitive substring). Filtering
precedes pagination; ordering is failed time descending, then ID descending.
Inspecting or filtering a letter never consumes it.

Replay atomically removes one letter and queues its original payload with a
fresh enqueue timestamp and zero retry attempts. It checks the event name,
distinct ID, and object properties JSON; invalid data remains in dead-letter
storage. A storage failure rolls back the move. Successful replay consumes
the original letter and appends work at the tail; it does not retain an audit
history or restore original ordering. `202` does not guarantee successful
processing. New processing items retain their admission identity and a bounded
transition history across replay; legacy rows have nullable links. See the
[runbook](runbooks/ingestion.md) for verification.

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

Persistence is asynchronous: 202 means accepted queue work. Due rows are ordered
by sequence within bounded per-project batches; delayed retries may be overtaken.
Malformed envelopes dead-letter immediately with one failed attempt. Recognized
SQLite BUSY/LOCKED failures wait 1, 2, 4, then 8 seconds before subsequent attempts;
the fifth failed attempt dead-letters with count five. Cancellation and unclassified
storage faults consume no attempt. Every worker mutation is fenced by a current
project owner/generation lease inside its transaction.

### Capture retry IDs, receipts, and capacity

Every single/batch event accepts optional nonempty UUID `event_id`. For seven
days after first admission, an equivalent retry of `(project,event_id)` returns
202 without new queue work. A conflicting payload returns **409** with
`code=event_id_payload_conflict` and rejects the entire batch. Equivalent repeated
IDs within a batch share one admission. Exact expiry permits a new admission;
retries do not extend expiry. Requests without IDs keep ordinary capture behavior.

Fingerprint v1 includes trimmed name/identity, supplied timestamp as UTC ticks
or an explicit omitted marker, and recursively sorted object properties. Arrays
retain order; scalar types and number spelling remain significant (`1` differs
from `1.0`). ID-bearing bodies reject duplicate object member names. Capture
bodies are bounded at 30 MiB and batches at 1000 items. Disabling
`Capture:IdempotencyEnabled` explicitly returns **503** for ID-bearing submissions.

`X-Capture-Receipt: true` requests durable progress metadata. Values must be
true/false; invalid values return **400**. With IDs or a requested receipt, 202
returns `{status,queued,deduplicated,receiptId,statusUrl}`; nullable receipt fields
are absent in meaning when no receipt was requested. With neither, the original
`{status,queued}` response remains. `Capture:ReceiptsEnabled=false` returns **503**
for receipt requests. A live legacy key without processing metadata makes an
entire receipt-enabled batch fail **409** with `legacy_processing_metadata_unavailable`.

| Route | Role | Contract |
| --- | --- | --- |
| `GET /api/projects/P/capture-receipts/R` | viewer | Receipt ID/project/created/completed/expiry, queued/processed/deadLettered counts, ordered items with ordinal/admission/state/replayGeneration/eventId/deadLetterId/retiredReason/changedAt; **404** unavailable/expired |
| `GET /api/projects/P/ingestion/limits` | viewer | `{maxPending,pending,paused}` |
| `PUT /api/projects/P/ingestion/limits` | admin | `{maxPending:null}` disables, or integer 1–100000; returns current settings/counts |

Receipt counts follow submitted positions, so duplicates may count multiple
positions referencing the same processing obligation. Item state and actual
event/dead-letter pointers commit with processing and queue acknowledgement.
Replay increments generation and returns the item to queued. A receipt has no
fixed expiry while any item is queued; after completion it expires seven days
after the final terminal transition. Later event retention may retire a pointer
without changing the original processed outcome. Status responses contain no
payload and require membership; capture write keys cannot read them.

Capacity counts all pending rows, including delayed work, after deduplication.
Capture/replay/limit changes serialize through one durable gate. Overflow returns
**429**, `Retry-After: 1`, and `code=queue_capacity_exceeded`, with no new admission.
Replay retains its letter on rejection. Lowering the limit never discards old
work; zero-new-work duplicate requests still fit. A project processing pause
returns **409** with `project_processing_paused`. Batch replay reports
`capacityExceeded`/`paused` per item alongside earlier outcomes and counts.

## Persons

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `GET /api/projects/{id}/persons` | member | — | **200** `[{ id, projectId, properties, distinctIds, createdAt }]` |
| `GET /api/projects/{id}/persons/count` | member | — | **200** `{ count }`, count of stored person rows in this project |
| `GET /api/projects/{id}/persons/{personId}` | member | — | **200** person / **404** |
| `GET /api/projects/{id}/persons/by-distinct-id/{distinctId}` | member | — | **200** person / **404** |
| `GET /api/projects/{id}/persons/{personId}/events` | viewer | query: `limit` 1–200 (default 50), `event` optional exact trimmed name ≤200, `cursor` | **200** `{events:[{id,event,timestamp,distinctId,properties}],nextCursor}` |
| `GET /api/projects/{id}/persons/{personId}/activity-summary` | viewer | query: required `from`, `to`, nonempty range ≤90 days | **200** `{personId,from,to,totalEvents,activeUtcDays,firstEventAt,lastEventAt,topEvents:[{event,count}]}` |
| `DELETE /api/projects/{id}/persons/{personId}` | admin | — | **202** `{job,statusUrl,invalidatesAllProjectExports:true}` starts durable erasure / **404** |
| `POST /api/projects/{id}/persons/{personId}/erasure` | admin | — | Same durable erasure contract as DELETE |
| `GET /api/projects/{id}/erasure-jobs/{jobId}` | admin | — | **200** `{job,unreadable,remainingReviewItems}`; review items contain identifiers/hash/time, never raw payload |
| `POST /api/projects/{id}/erasure-jobs/{jobId}/resume` | admin | — | **202** resumes a repaired failed job; **409** unless failed |
| `POST /api/projects/{id}/erasure-jobs/{jobId}/discard-unreadable` | admin | `{items:[{queueSequence?,deadLetterId?,contentHash}]}` | **202** explicitly discards 1–100 unchanged selected items; **409** stale/foreign selection; all validated before any deletion |

Erasure pauses project data operations with **503** `project_maintenance` and
invalidates every project export and copied snapshot input. A 202 response is
acceptance; poll until `completed`. `needsReview` and `failed` remain paused.
Known aliases remain suppressed afterward: capture/replay reject matches with
**422** `identity_suppressed`, including an identify event's anonymous alias.
Dedicated deployment HMAC keys are required; see [the erasure runbook](runbooks/person-erasure.md)
for configuration, key-version retention, explicit review, and scope limitations.

Person listing accepts `distinctId` (trimmed, max 400, exact case-sensitive alias
match), `createdFrom` (inclusive), and `createdBefore` (exclusive). Both dates
together require `createdFrom < createdBefore`; malformed or reversed/equal
bounds return 400. A person appears once even with multiple aliases. All filters
apply before pagination; creation time and ID determine stable order. The count
route counts all stored persons, independently of list filters or pagination.

Timeline events use canonical PersonId after identity merges, plus project
scope. They sort newest-first by timestamp then ID. Versioned base64url cursors
are bound to project/person/event filter and capped at 2,048 characters; invalid
or mismatched cursors return 400. Pagination is live, not a snapshot. Restart
from page one to see later insertions ahead of the current position.

Activity summaries include processed events in `[from,to)`, count distinct UTC
dates, and return at most five event names by count descending then ordinal name.
An empty person returns zero counts, null first/last, and empty topEvents; a
missing/foreign person returns 404. Active-day calculation loads timestamps
only, with memory proportional to matching events. Separate sequential reads
may observe concurrent ingestion at different moments; no frozen-report promise.

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
| `POST /api/projects/{id}/insights/preview` | member | `{ type, config }` | **200** `{ type, result }`; executes without storing an insight; **400** field validation errors |
| `GET /api/projects/{id}/insights/period-comparison` | member / read key | query: required `event`, `from`, `to`; duration >0 and <=90 days | **200** `{from,to,previousFrom,previousTo,currentCount,previousCount,delta,percentChange}` |
| `POST /api/projects/{id}/insights/multi-trend` | member / read key | `{events:[...],from,to,interval?}`, 1–5 unique trimmed names and explicit range <=90 days | **200** `{from,to,interval,series:[{event,buckets}],annotations}` |
| `PUT /api/projects/{id}/insights/{insightId}` | member | `{ name, type, config }`, all required | **200** replaced insight / **404**; identity and creation time remain unchanged |
| `GET /api/projects/{id}/insights/{insightId}/usages` | member | — | **200** `{ insightId, dashboardCount, tileCount, dashboards: [{id,name,tileCount}], truncated }` / **404** |
| `DELETE /api/projects/{id}/insights/{insightId}` | member | — | **204** unused insight removed; **409** with dashboardCount/tileCount if referenced; **404** missing/foreign insight |

Errors: 400 for missing `event`, bad `interval`, `from > to`, < 2 funnel
steps, out-of-range `windowDays`/`days`/`breakdownLimit`, invalid filters or
insight type.

Preview and replacement use a strict validator: named trend/funnel/retention
types, object config of at most 32 KiB UTF-8, explicit valid typed values, and
trend/funnel ranges with `from <= to` spanning at most 90 days. Funnels require
2–20 nonblank event names of at most 200 trimmed characters; numeric values
must be integers in their documented ranges. Retention `from` uses YYYY-MM-DD
and `days` is 1–60. Supplied filters must pass the existing array parser.
Omitted defaults remain supported; explicitly invalid values do not fall back.
Legacy saved creation retains its existing more permissive config contract.

Replacement trims name to 1–200 characters and replaces the complete config,
removing omitted old fields. It preserves omitted relative dates in storage;
preview resolves them against one captured current time. Existing dashboard
tiles follow the updated insight. Completed export documents retain their stored
bytes; pending insight exports use the config read when their worker executes.
Concurrent replacements use the last successful write. Config-size limits do
not bound how many events the existing query engine materializes.

Usage totals cover all project-scoped references; the detailed list is ordered
by dashboard name then ID and capped at 100, with `truncated=true` when needed.
The usage GET is an observation, not a reservation. Guarded deletion checks and
removes within a transaction; tile creation protects its existence check and
insert too. Completed exports survive deletion; pending exports can fail if
their source insight disappears before execution.

Buckets are zero-filled across the whole range, in UTC (weeks start Monday).
Funnels count each person's deepest step reached in order, with the
conversion window anchored at their earliest first-step event; timestamp ties
resolve by step order.

Period comparison counts event rows, including null PersonId, using adjacent
half-open windows: current `[from,to)`, previous of equal duration ending at
`from`. Event names contain 1–200 trimmed characters. Equal/reversed bounds and
an underflowing previous start return 400. Percentage is `delta*100/previousCount`,
rounded to two decimals away from zero at midpoints; it is null for a zero
previous count. This version has no filters or unique-person comparison.

Multi-trend preserves request order, case-sensitive event names, zero-filled
buckets, and existing inclusive trend boundaries and unique-person semantics.
Equal bounds are valid. Interval is hour/day/week (default day); numeric enum
aliases and duplicate names return 400. Annotations appear once. This version
has no filters/breakdown and no cross-query snapshot guarantee. Per-series
unique counts must not be added to report unique people across all series.

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
| `PUT /api/projects/{id}/feature-flags/{key}` | editor | any subset of `{ name, active, rolloutPercentage, filters, variants }`; required `If-Match` | **200** updated flag + new ETag |
| `DELETE /api/projects/{id}/feature-flags/{key}` | editor | required `If-Match` | **204** / **404** |

Flag response shape: `{ id, projectId, key, name, type, active,
rolloutPercentage, filters, variants, createdAt, revision }`.

Creation and cloning start at revision 1. Single-flag reads and successful
configuration writes return a strong ETag `"flag-ID-rREVISION"`. Updates and
deletes require one matching `If-Match`: missing **428**, malformed/weak/wildcard/
multiple **400**, another resource or stale revision **412**. Access and scoped
existence are checked first. A valid no-op update increments revision once.
Clients must fetch and reconcile after 412; do not blindly retry an old body
with a fresh token. This checkout enforces the precondition immediately, so
existing editing clients must send it. New configuration snapshots are limited
to 64 KiB UTF-8, including encoded name/type/active/rollout/filter/variant fields.

Management listing accepts `active=true|false` and `keyPrefix` (trimmed, max 200,
ASCII case-insensitive literal prefix). Both filters combine before pagination.
They do not change `/decide` or local-evaluation results. An active flag with
zero rollout still matches `active=true`.

### Flag history, restoration, scheduling, and management audit

Here `P` is `/api/projects/{projectId}`. See [project permissions](project-permissions.md).

| Method | Role | Contract |
| --- | --- | --- |
| `GET P/audit` | admin | Optional named `action`, `limit` 1–200 default 50, project/action-bound `cursor`; `{entries,nextCursor}` newest sequence first |
| `GET P/feature-flags/KEY/versions` | viewer | `limit` 1–100 default 20, optional positive `beforeRevision`; `{versions,nextBeforeRevision}` newest first |
| `POST P/feature-flags/KEY/restore` | editor | `{targetRevision}` + current `If-Match`; **200** flag + new ETag; missing retained version **404**, unsupported snapshot **409** |
| `POST P/feature-flags/KEY/rollout-schedule` | editor | `{executeAt,rolloutPercentage,expectedRevision}`; due 1 minute–30 days ahead, percentage 0–100; **202** schedule + Location |
| `GET P/feature-flags/KEY/rollout-schedule` | viewer | Latest schedule, states `pending/applied/cancelled/conflict/blocked`; **404** if absent |
| `DELETE P/feature-flags/KEY/rollout-schedule` | editor | Cancel pending schedule **204**; terminal **409**, absent **404** |

Versions expose typed configuration with `name,type,active,rolloutPercentage,
filtersJson,variantsJson`, time, actor/key IDs, origin, and restored-from revision.
Restoration preserves live key/ID/project/creation time and advances revision;
restoring revision 3 from current 8 produces 9. At most 100 snapshots remain per
live flag. Deletion removes snapshots but retains audit metadata. Migration
creates a baseline for each existing flag with unknown actor, preserving even
legacy malformed/oversized raw configuration; restoration rejects unsupported
configuration and missing/foreign cohort references. Restoring a cohort reference
does not restore its historical memberships.

Audit covers `member.added`, `member.role_changed`, `member.removed`,
`flag.created`, `flag.updated`, and `flag.deleted`. Each committed covered change
and its audit entry persist atomically; idempotent membership requests add no
extra entries. Project creation includes its creator membership. Failed writes
create no successful-change entry. Summaries contain role/revision/restore/
schedule metadata, not arbitrary input text, credentials, or person properties.
Entries carry actor user ID and optional personal-key ID from authentication.
No history is invented for pre-migration changes. This is application audit
history, not tamper-proof storage against direct database modification.

Schedules retain one latest row per live flag, replacing terminal metadata when
a new schedule is admitted. Pending admission conflicts return **409**; stale
expected revision **412**. At due time the processor rechecks the creator's current
editor membership and expected flag revision. It changes only percentage and
commits flag revision/history/audit with `applied`. Changed revision/deleted flag
becomes `conflict`; lost creator permission becomes `blocked`. Cancellation races
with execution: the first committed transition wins. A restart can delay work;
due time does not promise an exact commit time. `FlagScheduling:Enabled=false`
pauses execution and `FlagScheduling:AcceptNew=false` pauses new admission (**503**);
inspection and cancellation remain available. Stop old writers before migrating
and deploying this revision-aware implementation.

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

| Additional method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects/{id}/dashboards/{dashboardId}/duplicate` | member | `{ name }`, trimmed 1–200 characters | **201** dashboard and Location; new dashboard/tile IDs, existing insight IDs; **409** for more than 100 source tiles or unavailable insight references |
| `PUT /api/projects/{id}/dashboards/{dashboardId}/tile-layouts` | member | `{ tiles: [{ tileId, layout: { x,y,w,h,... } }] }`, 1–100 unique IDs | **200** updated dashboard; all changes commit together; **400** invalid input; **404** any unavailable dashboard/tile |
| `POST /api/projects/{id}/dashboards/{dashboardId}/refresh-selection` | member | `{ tileIds: [...] }`, 1–50 unique IDs | **200** existing refresh shape, selected tiles in request order; **400** invalid selection; **404** unavailable dashboard/tile before execution |
| `GET /api/projects/{id}/dashboards/{dashboardId}/template` | member | — | **200** portable version 1 document; **409** unsupported stored source; **404** missing source |
| `POST /api/projects/{id}/dashboards/import` | member | Version 1 template, at most 256 KiB | **201** new dashboard and Location; **400** invalid graph/config; **413** oversized body |

Duplication copies description and layout JSON and permits an empty source.
Insights remain shared: editing one changes the query used by both dashboards.
Strict batch layouts require integer `0 <= x < 12`, `0 <= y <= 10000`,
`1 <= w <= 12`, `1 <= h <= 10000`, and `x+w <= 12`. Extra fields survive,
overlap is allowed, and omitted tiles keep their layouts. Rejected batches
change nothing. Concurrent successful layout updates use the last committed
layout for an overlapping tile; no version check is introduced here.

Selected refresh reuses a shared insight's result/error within one request.
A later request recomputes. Broken stored configurations and missing/foreign
insight references produce per-tile errors while healthy tiles return results.
Cancellation propagates. The original full-dashboard refresh remains available.

A template has `{version:1,name,description,insights:[{ref,name,type,config}],
tiles:[{insightRef,layout}]}`. Refs are case-sensitive document-local names.
Import generates new dashboard, insight, and tile IDs while retaining sharing
within the document. Reimport creates another independent graph. Allow at most
50 insights and 100 tiles, names of 1–200 trimmed characters, descriptions up to
2000, and object layouts/configs. Duplicate refs, unused definitions, dangling
refs, unknown versions, and cohort-targeted filters are rejected. Configs use
the strict insight validator; relative date omissions remain relative. Source
project credentials and IDs are not exported as template identity fields.
Import requires destination membership and queries destination data.

## Annotations & data management

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `POST /api/projects/{id}/annotations` | member | `{ date: "YYYY-MM-DD", content }` | **201** `{ id, projectId, date, content, createdAt }` |
| `GET /api/projects/{id}/annotations?from=&to=` | member | — | **200** list, by date; annotations also ride along in trend responses covering their date |
| `PUT /api/projects/{id}/annotations/{annotationId}` | member | `{ date?, content? }` | **200** / **404** |
| `DELETE /api/projects/{id}/annotations/{annotationId}` | member | — | **204** / **404** |
| `GET /api/projects/{id}/event-definitions` | member | — | **200** `[{ name, firstSeenAt, lastSeenAt }]` — auto-populated on ingest |
| `GET /api/projects/{id}/property-definitions` | member | — | **200** `[{ name, propertyType, firstSeenAt, lastSeenAt }]` — `propertyType` is the first-observed JSON kind; system `$…` keys excluded |

Annotation content is trimmed and must contain 1–2000 characters when created
or supplied during update. Invalid content leaves all existing fields unchanged.
Listing accepts `contentContains` (trimmed, max 100, literal case-sensitive
substring). Date bounds are inclusive; `from > to` returns 400, while equal
dates are allowed. Ordering uses date, creation time, then ID.

Event definitions accept `namePrefix` (trimmed, max 200, ASCII case-insensitive
literal prefix). Property definitions accept `type=string|number|boolean|object|array`
(trimmed and case-insensitive). Unknown types return 400. These registry filters
apply before pagination and do not scan event properties or change the registry.

## Export

| Method & route | Auth | Request | Response |
| --- | --- | --- | --- |
| `GET /api/projects/{id}/export/events` | member | query: `format=csv\|json` (default json), `event`, `from`, `to`, `filters`, `cursor`, `limit` (1–1000, default 100) | **200** JSON `{ events: [{ id, timestamp, event, distinctId, personId, properties }], nextCursor }` or CSV; `nextCursor` also in the `X-Next-Cursor` header |
| `GET /api/projects/{id}/export/persons` | member | query: `format`, `cursor`, `limit` | **200** `{ persons: [{ id, createdAt, distinctIds, properties }], nextCursor }` or CSV |
| `GET /api/projects/{id}/export/insights/{insightId}` | member | query: `format` | **200** the insight's query result as JSON or CSV / **404**; 400 if the stored config fails to run |
| `POST /api/projects/{id}/exports` | member | `{ type: "events"\|"persons"\|"insight", format: "csv"\|"json", event?, from?, to?, filters?, insightId? }` | **202** job `{ id, projectId, type, format, status, rowCount, error, createdAt, completedAt }` + `Location` |
| `GET /api/projects/{id}/exports/{jobId}` | member | — | **200** job; `status`: `pending → running → completed \| failed` |
| `GET /api/projects/{id}/exports/{jobId}/download` | member | — | **200** the document (`text/csv` or `application/json`) / **409** if not `completed` / **404** |

Cursors are opaque base64 of `(timestamp ticks, id)`; pages use the same
`(timestamp, id)` order and continuation predicate. Ties are handled without
skipping unchanged rows. These are live pages, not snapshots: late inserts
before an already-consumed position require restarting the traversal.
Property filters apply after the SQL page scan, so a filtered
page may hold fewer than `limit` rows while the cursor still advances. Async
exports are capped at 50,000 rows. Errors: 400 (bad `format`, invalid
`cursor`, bad job `type`, insight export without `insightId`, invalid
filters).

Synchronous event exports reject `from > to` with 400 before selecting CSV or
JSON output. Equal bounds remain a valid inclusive instant. Error responses
remain problem JSON even when CSV was requested.

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

## Audience editing and snapshots

Here `P` means `/api/projects/{projectId}` and `C` means a cohort ID. These routes
use the role matrix and do not accept project read/write keys as management auth.

| Method & route | Role | Request | Response |
| --- | --- | --- | --- |
| `POST P/cohorts/preview` | viewer | `{rules:[...],sampleLimit?:10}` | **200** `{evaluatedAt,count,samplePersonIds}` |
| `PUT P/cohorts/C/rules` | editor | `{rules:[...]}` | **200** CohortResponse; static source **409** |
| `POST P/cohorts/C/snapshot` | editor | `{name}` trimmed 1–200 | **201** `{cohort,memberCount,evaluatedAt}` and new cohort Location |
| `PUT P/cohorts/C/persons` | editor | `{personIds:[...]}` | **200** `{added,removed,unchanged,total}` |

Preview and rule replacement require 1–10 parsed rules and at most 32 KiB UTF-8
rules JSON. Preview samples 1–50 IDs (default 10) after computing the complete
AND intersection, using .NET GUID ordering. It writes nothing. Rules share one
evaluation time and preserve existing inclusive lower/future-event behavior.
Property-rule evaluation still scans project person properties; sampling bounds
output, not all input work. Rule replacement keeps ID/name/type/creation time
and uses last successful write semantics.

Snapshot requires a dynamic source and at most 1,000 matches. Static sources,
oversized results, or unavailable evaluated people return 409 with no copy.
Source reads, evaluation, and new static cohort/member writes share one transaction.
Empty membership is valid. The copy freezes person IDs, not properties/events;
later person deletion can remove membership.

Static member replacement accepts at most 1,000 raw IDs before deduplication.
Null/missing list is 400; empty clears. Any unknown/foreign person produces generic
400 before mutation; a dynamic cohort is 409. Deletions/additions commit together.
Repeating the same set returns zero added/removed. Existing add-known-people
routes retain their separate, more permissive behavior.

## Flag diagnostics, drafts, and read-key rotation

| Method & route | Role | Request | Response |
| --- | --- | --- | --- |
| `POST P/feature-flags/{key}/explain` | viewer | `{distinctId}` trimmed 1–400 | **200** `{key,value,personResolved,stages:[{stage,outcome,reason}],warnings}` |
| `POST P/feature-flags/evaluate-batch` | viewer | `{distinctIds:[...],keys:[...]}` | **200** `{results:[{distinctId,featureFlags:{...}}]}` |
| `POST P/feature-flags/{key}/clone` | editor | `{key,name}` both 1–200; key ASCII letters/digits/`-`/`_` | **201** inactive FeatureFlagResponse; duplicate/invalid stored source **409** |
| `POST P/read-key/rotate` | admin | `{expectedReadKey}` current generated `rk_live_` + 32 lowercase hex characters | **200** `{projectId,readKey}` with `Cache-Control: no-store`; stale **409** |

Explanation shares `/decide` evaluation, with stages active/targeting/rollout/variant.
An early failure skips later stages. Warnings describe inherited invalid-targeting
unrestricted and invalid-variant boolean-on fallbacks. No person properties or
cohort lists are returned; this explains current state, not a historical decision.

Batch accepts 1–50 unique trimmed identities ≤400 and 1–20 unique trimmed exact
keys ≤200. Duplicates and unknown/foreign keys are 400 before evaluation. Unknown
identities are valid and create no person. Input identity order is preserved.
Shared person and cohort reads are reused within this call only; the next call
reads current state. Each alias retains its own distinct-ID hash input.

Clones get a new ID/time, retain configuration, and are always inactive. Changing
the key changes rollout/variant hashing, so later activation need not reproduce
source assignments. Read-key rotation changes only that credential, with the
expected key in the UPDATE predicate. Old-key checks fail after commit, while
already-authorized requests may finish. Write keys and login sessions remain valid.

## Capture preview and selected recovery

| Method & route | Role | Request | Response |
| --- | --- | --- | --- |
| `POST P/capture/validate` | viewer | Existing single/batch capture fields; no `api_key` property | **200** `{valid:true,eventCount,preview:[{event,distinctId,timestamp,properties}],previewTruncated}` |
| `GET P/ingestion/dead-letters/{letterId}/replay-check` | viewer | — | **200** `{letterId,replayable,issues:[{code,field,message}]}` |
| `POST P/ingestion/dead-letters/replay-batch` | admin | `{letterIds:[...]}` 1–20 unique nonempty GUIDs | **200** `{results:[{letterId,outcome}],queued,invalid,notFound}` |

Capture preview has a 1 MiB body cap (413), the existing 1,000-item batch cap,
shared indexed validation, and at most three normalized preview events. Omitted
timestamps stay null, batch takes precedence, and non-object properties become
`{}`. Explicit null batch items are 400. A supplied credential property is 400,
even when null. The no-store response and all validation paths perform no writes.
This proves admission shape, not successful future identity/database processing.

Replay check distinguishes `invalid-envelope-json`, `missing-envelope`,
`missing-event-name`, `missing-distinct-id`, `missing-properties-json`,
`invalid-properties-json`, and `properties-not-object`, without echoing payloads.
It validates the stored IncomingEvent format, not public capture JSON. Checking
does not reserve a letter. Single replay retains its 422 invalid-envelope policy.

Batch results preserve input order with outcomes `queued`, `invalidPayload`,
`notFound`, `capacityExceeded`, `paused`, or `suppressed`; foreign IDs are notFound
when project availability permits lookup. Each item owns its transaction. On an
unexpected failure or cancellation, earlier commits remain and later items may
be unattempted. A retry can see consumed letters as notFound. Accepted replay
does not establish successful event processing.

## Export history, retry, deletion, and integrity

| Method & route | Role | Request | Response |
| --- | --- | --- | --- |
| `POST P/exports/{jobId}/retry` | editor | No body | **202** `{sourceJobId,job}` with new Location; only Failed source, otherwise **409** |
| `GET P/exports` | viewer | `status`, `type`, `limit` 1–100 default 25, `cursor` | **200** `{jobs:[ExportJobResponse],nextCursor}` |
| `DELETE P/exports/{jobId}` | editor | — | **204** for Completed/Failed/Cancelled; Pending/Running/CancelRequested **409**; missing **404** |
| `POST P/exports/{jobId}/cancel` | editor | — | Pending cancellation **200**; Running cancellation requested **202**; completed/failed **409**; repeated cancellation idempotent |
| `GET P/exports/{jobId}/integrity` | viewer | — | **200** `{jobId,contentType,byteLength,sha256,encoding:"utf-8"}` |

Retry creates a new row from exact Type/Format/ParamsJson/Consistency, leaving the
old error, completion time, and output unchanged. Repeated retries create distinct
jobs and capture fresh input; recovery of the same snapshot job reuses its stored
copy. History filters are named pending/running/completed/failed/cancelRequested/cancelled and events/persons/insight;
blank means omitted. Sorting is CreatedAt/ID descending, with a project/filter-bound
versioned cursor capped at 2,048 characters. SQL selects metadata without parameters
or inline content. Status changes can alter live list membership between pages.

Deletion enforces terminal state in the database statement. Future lookups fail
after deletion; an already-loaded download may finish. Source analytics data is
untouched. Integrity hashes exact stored UTF-8 document bytes without BOM, preserving
Unicode, whitespace, CSV quoting, and line endings. Download uses the same encoding.
Empty string is valid; null content or noncompleted status returns 409. Hashing
allocates/work scales with document size and excludes transport compression/framing.

## Discovery and project observations

| Method & route | Role | Query | Response |
| --- | --- | --- | --- |
| `GET P/event-usage` | viewer | required `from`,`to`, nonempty `[from,to)` ≤90 days; `limit` 1–100 default 20 | **200** `{from,to,totalEvents,eventNameCount,items:[{event,count,firstAt,lastAt}]}` |
| `GET P/property-values` | viewer | required `event`,`property` literal top-level names 1–200, `[from,to)` ≤31 days; `limit` 1–25 default 10 | **200** `{event,property,totalEvents,values:[{value,count}],missingCount,nullCount,nonStringCount,otherStringCount}` |
| `GET P/ingestion/status` | viewer | `maxPendingAgeSeconds` 1–3600 default 60; `maxPending` 1–100000 default 1000 | **200** `{status,observedAt,metrics,thresholds,reasons}` |
| `GET P/overview` | viewer | — | **200** `{project:{id,name,createdAt},observedAt,counts:{persons,events,insights,dashboards,cohorts,featureFlags,activeFeatureFlags,completedExports},ingestion}` |

Usage aggregates processed Events, including stored system events; registry-only
names are omitted. Full totals precede the top limit. Grouped metadata sorts by
count descending then .NET ordinal name; event properties are not loaded.
Property discovery distinguishes exact case-sensitive strings, missing, null,
and non-string values. A real `(other)` string remains a value. More than 10,000
rows or 8 MiB scanned properties returns 422 with no partial statistics; malformed
stored documents return 409. One large stored string must still be read to measure it.

Ingestion status is `attention` when any dead letters exist or either threshold
is met inclusively; reasons are ordered `dead_letters_present`, `queue_depth_high`,
`oldest_pending_too_old`. Otherwise it is `ok`. This is an observation, not a worker
heartbeat or alert history. Overview counts stored entities rather than aliases,
tiles, or queued work. Active flags count Active=true independently of rollout.
Its dedicated metadata DTO never includes credentials, even for admins. Multiple
sequential count reads are live observations, not a frozen cross-table snapshot.


## Bounded trends and person sessions

`GET P/insights/trend-bounded` accepts a member or project read key and returns
the existing trend shape `{event,interval,from,to,buckets,annotations}`. Require
`event` (trimmed 1–200), explicit nonempty `from`/`to` within 90 days, and named
`interval=hour|day|week`. Boundaries remain inclusive. Optional `filters` support
event properties only; person/cohort filters and breakdown are 400.

Limits are 10,000 scanned rows, 8 MiB projected property JSON, 2,161 buckets,
and 1,000 annotations. Scanned work is charged before application filtering;
unused property documents are omitted from SQL. Overflow returns 422 with
`reason` equal to `row_limit`, `byte_limit`, `bucket_limit`, or `annotation_limit`
and narrowing guidance. A linked two-second execution deadline returns 504
`reason:deadline`; client cancellation remains cancellation. Provider cleanup
can take longer than two seconds, and one oversized value may be allocated
before its bytes are checked. A 200 response always contains the complete
within-budget result. Existing trend routes retain their prior behavior.

`GET P/persons/U/sessions?from=...&to=...&gapMinutes=30` requires a current member
with Viewer and `analytics:read` when using a restricted token; project read keys
are not accepted. Require a nonempty half-open `[from,to)` range of at most seven
days; gap is 1–120 minutes, default 30. Missing/foreign person is 404; an existing
person with no selected events returns an empty list.

Response is `{personId,from,to,gapMinutes,boundarySemantics:"window-local",sessions}`.
Each session has `ordinal`, `firstTimestamp`, `lastTimestamp`, `firstEventId`,
`lastEventId`, `eventCount`, `observedDurationSeconds`, `mayStartBeforeWindow`,
and `mayContinueAfterWindow`. Events use current canonical PersonId, timestamp/ID
order, and a gap **greater than or equal to** the threshold starts a new session.
The first/last flags are conservative because there is no lookbehind/lookahead.
Ordinals belong to this response, not durable session identities.

```text
Window: [10:00,11:00), gap: 30 minutes
10:00 ------ 10:10                  10:40             11:00
  | session 1 |                      | session 2 |      excluded
  2 events, observed 600 seconds      1 event, 0 seconds
  mayStartBeforeWindow=true          mayContinueAfterWindow=true
```

Sessions cap input at 10,000 rows and output at 500 sessions; excess returns
422 `row_limit` or `session_limit`, never a truncated success. They share the
two-second deadline/client-cancellation distinction. Observed duration does not
mean total visit duration. Late events and identity merges can change results.
See bootcamp [session 23](../astradocs/bootcamp/23-bounded-trends.md) and
[session 24](../astradocs/bootcamp/24-person-sessions.md).

## Hourly alert rules and in-app notifications

| Method and route | Minimum role / token scope | Contract |
| --- | --- | --- |
| `GET P/alert-rules` | Viewer / configuration:read | `{items,limit,offset}`; limit 1–100 default 50, offset >=0 (clamped) |
| `GET P/alert-rules/R` | Viewer / configuration:read | Rule configuration, revision, nextWindowStart, skippedThrough, timestamps |
| `POST P/alert-rules` | Editor / configuration:write | `{name,eventName,threshold,enabled}`; 201, max 20 undeleted rules/project |
| `PUT P/alert-rules/R` | Editor / configuration:write | Full configuration plus current `revision`; 200, stale409 |
| `DELETE P/alert-rules/R?revision=N` | Editor / configuration:write | 204 soft delete; stale409, missing404 |
| `GET P/notifications` | Viewer / configuration:read | `{items,limit,offset}` ordered newest first, caller-specific nullable readAt |
| `PUT P/notifications/N/read` | Viewer / configuration:read | No body required; 200 `{notificationId,readAt}`, idempotent for authenticated user |

Names/event names are trimmed 1–200 characters; thresholds are 1–1,000,000.
New rules default to disabled if enabled is omitted. Revision changes start with
the next complete UTC hour; an exact-hour edit may use that hour. Count >= threshold
triggers, and each half-open hour is evaluated once per revision. Evaluation,
notification creation, and progress commit together. At most ten windows are
processed per cycle and at most 24 hours of missed history are caught up; older
history is explicitly skipped. Late events do not reopen an evaluated window.

Notifications retain event/rule/window/count facts, not event properties. Marking
one read never marks another user's state; callers cannot select a UserId through
the body. Membership removal removes visibility. There is no external messaging.
See [the alert runbook](runbooks/hourly-alerts.md) for scheduler configuration,
recovery, and the bootcamp explanation.
