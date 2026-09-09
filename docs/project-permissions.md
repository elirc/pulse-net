# Project permission matrix

**SR-02 enforcement is implemented and its focused acceptance checks passed.**
Check the [bootcamp ledger](../astradocs/bootcamp/stories.json) for actual evidence.
The role migration must run before starting this version. This table specifies
the minimum role used by the current route guards.

The 418-test integration run passed 417 tests; its sole failure was a separate
one-second rate-window test. Final full-suite verification remains pending.
New feature routes also appear in the central source matrix and its inventory test.

Roles are ordered viewer, editor, admin. A higher role includes lower-role
capabilities. Authentication identifies the user; membership determines project
visibility; the role determines permitted actions; credential type may impose
additional restrictions. These checks are related but do different jobs.

The scope column defines SR-16's restricted-token intersection. Scopes do not
grant access above the current role. A dash means a restricted personal token
cannot call that route. Scope/authentication checks passed 90 focused tests;
final integration evidence is tracked in the bootcamp ledger.

`P` means `/api/projects/{projectId}`; `D`, `I`, `C`, `F`, `U`, and `J` mean a
dashboard, insight, cohort, flag key, user, and export-job identifier. Child
lookups remain project-scoped regardless of role.

| Method | Route | Minimum role | Restricted-token scope | Existing read-key access |
| --- | --- | --- | --- | --- |
| GET | `P` | viewer | configuration:read | No |
| PUT | `P` | admin | — | No |
| GET | `P/members` | viewer | — | No |
| POST | `P/members` | admin | — | No |
| PUT | `P/members/U/role` (new) | admin | — | No |
| DELETE | `P/members/U` (new) | admin | — | No |
| GET | `P/persons` | viewer | analytics:read | No |
| GET | `P/persons/count` | viewer | analytics:read | No |
| GET | `P/persons/{personId}` | viewer | analytics:read | No |
| GET | `P/persons/{personId}/events` | viewer | analytics:read | No |
| GET | `P/persons/{personId}/activity-summary` | viewer | analytics:read | No |
| GET | `P/persons/{personId}/sessions` | viewer | analytics:read | No |
| GET | `P/persons/by-distinct-id/{distinctId}` | viewer | analytics:read | No |
| DELETE | `P/persons/{personId}` | admin | — | No |
| POST | `P/persons/{personId}/erasure` | admin | — | No |
| GET | `P/erasure-jobs/{jobId}` | admin | — | No |
| POST | `P/erasure-jobs/{jobId}/resume` | admin | — | No |
| POST | `P/erasure-jobs/{jobId}/discard-unreadable` | admin | — | No |
| GET | `P/insights/trend` | viewer | analytics:read | Yes |
| GET | `P/insights/trend-bounded` | viewer | analytics:read | Yes |
| POST | `P/insights/funnel` | viewer | analytics:read | Yes |
| GET | `P/insights/retention` | viewer | analytics:read | Yes |
| POST | `P/insights/preview` | viewer | analytics:read | No |
| GET | `P/insights/period-comparison` | viewer | analytics:read | Yes |
| POST | `P/insights/multi-trend` | viewer | analytics:read | Yes |
| GET | `P/insights` | viewer | configuration:read | No |
| GET | `P/insights/I` | viewer | configuration:read | No |
| POST | `P/insights` | editor | configuration:write | No |
| PUT | `P/insights/I` | editor | configuration:write | No |
| DELETE | `P/insights/I` | editor | configuration:write | No |
| GET | `P/insights/I/usages` | viewer | configuration:read | No |
| GET | `P/dashboards` | viewer | configuration:read | No |
| GET | `P/dashboards/D` | viewer | configuration:read | No |
| POST | `P/dashboards` | editor | configuration:write | No |
| PUT | `P/dashboards/D` | editor | configuration:write | No |
| DELETE | `P/dashboards/D` | editor | configuration:write | No |
| POST | `P/dashboards/D/tiles` | editor | configuration:write | No |
| PUT | `P/dashboards/D/tiles/{tileId}` | editor | configuration:write | No |
| DELETE | `P/dashboards/D/tiles/{tileId}` | editor | configuration:write | No |
| PUT | `P/dashboards/D/tile-layouts` | editor | configuration:write | No |
| POST | `P/dashboards/D/duplicate` | editor | configuration:write | No |
| GET | `P/dashboards/D/template` | viewer | configuration:read | No |
| POST | `P/dashboards/import` | editor | configuration:write | No |
| POST | `P/dashboards/D/refresh` | viewer | analytics:read | No |
| POST | `P/dashboards/D/refresh-selection` | viewer | analytics:read | No |
| GET | `P/cohorts` | viewer | configuration:read | No |
| GET | `P/cohorts/C` | viewer | configuration:read | No |
| POST | `P/cohorts` | editor | configuration:write | No |
| DELETE | `P/cohorts/C` | editor | configuration:write | No |
| GET | `P/cohorts/C/persons` | viewer | analytics:read | No |
| POST | `P/cohorts/C/persons` | editor | configuration:write | No |
| DELETE | `P/cohorts/C/persons/{personId}` | editor | configuration:write | No |
| GET | `P/feature-flags` | viewer | configuration:read | No |
| GET | `P/feature-flags/F` | viewer | configuration:read | No |
| GET | `P/feature-flags/local-evaluation` | viewer | configuration:read | Yes |
| POST | `P/feature-flags` | editor | configuration:write | No |
| PUT | `P/feature-flags/F` | editor | configuration:write | No |
| DELETE | `P/feature-flags/F` | editor | configuration:write | No |
| GET | `P/annotations` | viewer | configuration:read | No |
| POST | `P/annotations` | editor | configuration:write | No |
| PUT | `P/annotations/{annotationId}` | editor | configuration:write | No |
| DELETE | `P/annotations/{annotationId}` | editor | configuration:write | No |
| GET | `P/event-definitions` | viewer | analytics:read | No |
| GET | `P/property-definitions` | viewer | analytics:read | No |
| GET | `P/ingestion/metrics` | viewer | analytics:read | No |
| GET | `P/ingestion/dead-letters` | viewer | analytics:read | No |
| GET | `P/ingestion/dead-letters/{letterId}` | viewer | analytics:read | No |
| POST | `P/ingestion/dead-letters/{letterId}/replay` | admin | — | No |
| GET | `P/export/events` | viewer | analytics:read | No |
| GET | `P/export/persons` | viewer | analytics:read | No |
| GET | `P/export/insights/I` | viewer | analytics:read | No |
| POST | `P/exports` | editor | exports:write | No |
| GET | `P/exports/J` | viewer | analytics:read | No |
| GET | `P/exports/J/download` | viewer | analytics:read | No |
| POST | `P/cohorts/preview` | viewer | analytics:read | No |
| PUT | `P/cohorts/C/rules` | editor | configuration:write | No |
| POST | `P/cohorts/C/snapshot` | editor | configuration:write | No |
| PUT | `P/cohorts/C/persons` | editor | configuration:write | No |
| POST | `P/feature-flags/F/explain` | viewer | configuration:read | No |
| POST | `P/feature-flags/evaluate-batch` | viewer | configuration:read | No |
| POST | `P/feature-flags/F/clone` | editor | configuration:write | No |
| POST | `P/read-key/rotate` | admin | — | No |
| POST | `P/capture/validate` | viewer | analytics:read | No |
| GET | `P/ingestion/dead-letters/{letterId}/replay-check` | viewer | analytics:read | No |
| POST | `P/ingestion/dead-letters/replay-batch` | admin | — | No |
| GET | `P/exports` | viewer | analytics:read | No |
| POST | `P/exports/J/retry` | editor | exports:write | No |
| DELETE | `P/exports/J` | editor | exports:write | No |
| GET | `P/exports/J/integrity` | viewer | analytics:read | No |
| GET | `P/event-usage` | viewer | analytics:read | No |
| GET | `P/property-values` | viewer | analytics:read | No |
| GET | `P/ingestion/status` | viewer | analytics:read | No |
| GET | `P/overview` | viewer | analytics:read | No |
| GET | `P/audit` | admin | — | No |
| GET | `P/feature-flags/F/versions` | viewer | configuration:read | No |
| POST | `P/feature-flags/F/restore` | editor | configuration:write | No |
| GET | `P/feature-flags/F/rollout-schedule` | viewer | configuration:read | No |
| POST | `P/feature-flags/F/rollout-schedule` | editor | configuration:write | No |
| DELETE | `P/feature-flags/F/rollout-schedule` | editor | configuration:write | No |
| GET | `P/capture-receipts/{receiptId}` | viewer | analytics:read | No |
| GET | `P/ingestion/limits` | viewer | analytics:read | No |
| PUT | `P/ingestion/limits` | admin | — | No |
| POST | `P/exports/J/cancel` | editor | exports:write | No |
| GET | `P/retention` | admin | — | No |
| PUT | `P/retention` | admin | — | No |
| GET | `P/retention/preview` | admin | — | No |
| GET | `P/retention/runs` | admin | — | No |
| GET | `P/alert-rules` and `P/alert-rules/{ruleId}` | viewer | configuration:read | No |
| POST | `P/alert-rules` | editor | configuration:write | No |
| PUT / DELETE | `P/alert-rules/{ruleId}` | editor | configuration:write | No |
| GET | `P/notifications` | viewer | configuration:read | No |
| PUT | `P/notifications/{notificationId}/read` | viewer | configuration:read | No |

## Routes outside a project-role check

| Route | Intended authentication/capability |
| --- | --- |
| `POST /capture` | Existing project write key; roles do not convert it into a management token |
| `POST /decide` | Existing project write key; unchanged SDK capability |
| `GET /health` | Public operational probe |
| `GET /api/ingestion/metrics` | Existing public process-wide counters |
| `POST /api/auth/register`, `POST /api/auth/login` | Public account authentication |
| `GET /api/auth/me` | Authenticated user's own account; SR-16 permits this explicit global read |
| `GET /api/projects` | Authenticated user's visible projects; SR-16 further filters allowed projects |
| `POST /api/projects` | Authenticated user; creator becomes admin; restricted tokens denied under SR-16 |
| Personal-key creation/list/deletion | Actual JWT session required for legacy and restricted paths |

## Credential and membership invariants

During person erasure, authorized project data operations return 503
`project_maintenance`. Admin erasure job operations and ingestion status remain
available. Capture and decide also enforce the pause with their existing write-key
authentication. Erasure initiation requires Admin and is unavailable to restricted
personal tokens. See [the erasure runbook](runbooks/person-erasure.md).

Project write/read keys must be absent from serialized viewer/editor responses,
including project lists and rename/detail responses. SR-16 additionally removes
those fields for every restricted-token response even when its user is an admin.
Otherwise a restricted token could recover a broader credential.

Existing memberships migrate to admin to preserve their pre-role privileges.
New project creators are admins; invitations default to viewer. An operator
can deliberately lower migrated roles after reviewing the new access model.

Role changes and removals must serialize through a project-row write inside a
transaction, then recheck the acting member and current administrator count.
A project must retain at least one admin after every successful mutation.
Return 400 for invalid named roles, 404 for invisible/missing membership, 403
for a visible member lacking permission, and 409 for the last-admin invariant.

Read-only POSTs demonstrate why permissions cannot be inferred from HTTP verbs.
Previewing a query and refreshing a dashboard are viewer operations; replaying
a dead letter is an admin operation even though all three use POST.

## Verification before enforcement is considered complete

Inventory every mapped route against the central matrix. Exercise each role,
outsiders, existing read/write key paths, personal keys, credential serialization,
next-request role changes, legacy backfill, and competing last-admin mutations.
Future endpoints must be classified explicitly and covered by the route-inventory
check. An unclassified route must not accidentally grant a restricted token access.
