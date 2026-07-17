# Getting started

A walkthrough from clean checkout to answering real analytics questions with
curl. Full endpoint details live in the [API reference](api-reference.md).

## Prerequisites

- .NET 10 SDK
- `curl` (examples below); `jq` is handy but optional

## Run

```bash
dotnet build
dotnet test                                    # 291 tests, all green
dotnet run --project src/Pulse.Api            # http://localhost:5141 (launchSettings)
```

The API creates its SQLite database (`pulse.db`) on first start. Delete the
file to reset. Configuration knobs: `ConnectionStrings__Pulse`, `Jwt:Secret`
(preconfigured for Development), `Jwt:LifetimeMinutes` (default 480),
`RateLimiting:Capture:PermitLimit`/`WindowSeconds` (default 300/60).

In the examples below, set the base URL once:

```bash
BASE=http://localhost:5141    # match the port `dotnet run` prints
```

## Seed demo data (optional shortcut)

```bash
dotnet run --project src/Pulse.Api -- seed
```

This creates a demo project with 30 days of simulated traffic and prints the
project id, `pk_live_…` write key, `rk_live_…` read key and a demo login,
then exits. Start the API afterwards and you can jump straight to
[querying](#run-queries).

## Create an account and a project

```bash
# Register — the response includes a JWT.
TOKEN=$(curl -s $BASE/api/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"ada@example.com","password":"correct-horse-battery","name":"Ada"}' \
  | jq -r .token)

# Create a project — note the two keys in the response.
curl -s $BASE/api/projects \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"My App"}' | jq
# { "id": "…", "apiKey": "pk_live_…", "readKey": "rk_live_…", … }

PROJECT=<id from above>
WRITE_KEY=<apiKey from above>
```

## Capture events

Capture is asynchronous: the endpoint validates the shape, queues the events
durably and returns **202**; a background worker persists them within
milliseconds (watch `GET /api/ingestion/metrics` → `pending: 0`).

```bash
# Anonymous browsing, then signup + $identify, then a purchase.
curl -s $BASE/capture -H 'Content-Type: application/json' -d '{
  "api_key": "'$WRITE_KEY'",
  "batch": [
    { "event": "pageview", "distinct_id": "device-1",
      "timestamp": "2026-03-01T10:00:00Z", "properties": { "url": "/pricing" } },
    { "event": "signup", "distinct_id": "device-1",
      "timestamp": "2026-03-01T10:05:00Z",
      "properties": { "$set": { "plan": "free" },
                       "$set_once": { "initial_referrer": "google" } } },
    { "event": "$identify", "distinct_id": "user-ada",
      "timestamp": "2026-03-01T10:05:01Z",
      "properties": { "$anon_distinct_id": "device-1" } },
    { "event": "purchase", "distinct_id": "user-ada",
      "timestamp": "2026-03-02T09:00:00Z", "properties": { "amount": 49 } }
  ]}' | jq
# { "status": "queued", "queued": 4 }
```

The `$identify` merges `device-1` and `user-ada` into one person; check it:

```bash
curl -s $BASE/api/projects/$PROJECT/persons/by-distinct-id/user-ada \
  -H "Authorization: Bearer $TOKEN" | jq '.distinctIds, .properties'
# ["device-1","user-ada"]  { "plan": "free", "initial_referrer": "google" }
```

## Run queries

Query endpoints accept your JWT — or the project **read key** via the
`X-Api-Key` header, shown here so the examples work from any script:

```bash
READ_KEY=<readKey from project creation>

# Trend: daily pageviews, zero-filled buckets.
curl -s "$BASE/api/projects/$PROJECT/insights/trend?event=pageview&from=2026-03-01T00:00:00Z&to=2026-03-07T23:59:59Z&interval=day" \
  -H "X-Api-Key: $READ_KEY" | jq '.buckets'

# Trend with a property filter and a breakdown.
FILTERS='[{"property":"url","operator":"equals","value":"/pricing"}]'
curl -s "$BASE/api/projects/$PROJECT/insights/trend?event=pageview&interval=day&breakdown=url&filters=$(jq -rn --arg f "$FILTERS" '$f|@uri')" \
  -H "X-Api-Key: $READ_KEY" | jq '.series'

# Funnel: how many people signed up and then purchased within 14 days?
curl -s $BASE/api/projects/$PROJECT/insights/funnel \
  -H "X-Api-Key: $READ_KEY" -H 'Content-Type: application/json' \
  -d '{"steps":["signup","purchase"],"from":"2026-03-01T00:00:00Z","to":"2026-03-14T00:00:00Z","windowDays":14}' \
  | jq '.steps[] | {event, persons, conversionFromFirst}'

# Retention: who came back on day 1, 2, … after their first event?
curl -s "$BASE/api/projects/$PROJECT/insights/retention?from=2026-03-01&days=7" \
  -H "X-Api-Key: $READ_KEY" | jq '.cohorts'
```

## Evaluate a feature flag via /decide

```bash
# Create a flag at 50% rollout (management API → JWT).
curl -s $BASE/api/projects/$PROJECT/feature-flags \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"key":"new-onboarding","type":"boolean","rolloutPercentage":50}' | jq

# Evaluate all flags for a user (SDK API → write key).
curl -s $BASE/decide -H 'Content-Type: application/json' \
  -d '{"api_key":"'$WRITE_KEY'","distinct_id":"user-ada"}' | jq
# { "featureFlags": { "new-onboarding": true } }   ← same answer every time
```

The rollout is a deterministic hash of `flagKey.distinct_id`: the same user
always gets the same answer, and raising the percentage only ever adds users.
Multivariate flags return the variant key instead of `true`; targeting
filters (person properties, cohorts) can gate flags to a segment.

## Where to next

- Save an insight (`POST /api/projects/{id}/insights`), pin it to a dashboard
  and `POST …/refresh` to run every tile at once.
- Export events as CSV: `GET /api/projects/{id}/export/events?format=csv`,
  or create an async job with `POST /api/projects/{id}/exports` and poll it.
- Inspect ingestion health: `GET /health`, `GET /api/ingestion/metrics`, and
  per-project dead letters.
- Read the [architecture overview](architecture.md) and the
  [ADRs](adr/) for the reasoning behind the design.
