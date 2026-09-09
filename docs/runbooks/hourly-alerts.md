# Hourly event-count alerts

Editors create rules with POST `/api/projects/P/alert-rules` and `{name,eventName,threshold,enabled}`. Rules default to disabled when enabled is omitted. Names are trimmed to 1–200 characters, threshold is 1–1,000,000, and a project can have 20 undeleted rules. GET lists/details require Viewer; all routes also enforce their named restricted-token scope in the [permission matrix](../project-permissions.md).

PUT `/api/projects/P/alert-rules/R` sends complete configuration plus current `revision`. DELETE uses `?revision=N`. A stale revision returns 409, a missing/deleted rule returns 404, and an invalid revision returns 400. Deletion disables the rule and preserves past evaluations/notifications. Revisions must leave room for their increment.

Each new revision begins at the next full UTC hour: a 10:15 edit begins with 11:00–12:00 after noon; an exact 10:00 edit may use 10:00–11:00. Windows are half-open and trigger when observed count is at least threshold. A late event arriving after evaluation does not reopen that window. Counts describe data visible when the worker evaluates, not an ingestion snapshot.

The worker processes at most ten due windows per cycle, with a 24-hour catch-up horizon. Older missed windows are recorded through `skippedThrough`, not claimed as evaluated. `nextWindowStart` identifies remaining work. A paused project is excluded from selection and checked again inside the evaluation transaction, so it does not block another project's eligible alerts.

Set `HourlyAlerts:Enabled=false` to pause the scheduler. `HourlyAlerts:SweepSeconds` defaults to 60 and is clamped to 1–3600. Reenabling resumes from persisted progress under the same catch-up limit. No email, Slack, webhook, or external delivery is performed.

GET `/api/projects/P/notifications?limit=50&offset=0` returns `{items,limit,offset}`. Both rule and notification lists clamp limit to 1–100 and offset to zero or higher. Each notification contains immutable rule/event/window/count/threshold facts and the caller's nullable `readAt`. PUT `/api/projects/P/notifications/N/read` requires no body and marks only the authenticated user's state. Repetition preserves the original read time. Removing project membership removes inbox access.

If a worker fails, check its error and persisted rule cursor. Evaluation, notification, and progress commit together. Retry after fixing the cause; do not manually create a notification from a log line. Unique evaluation and delivery keys prevent duplicates. See [bootcamp session 25](../../astradocs/bootcamp/25-hourly-alerts.md) for crash experiments and a four-record diagram.
