# Hourly alerts: durable time, evidence, and delivery

SR-20 adds one deliberately narrow alert: count an exact event name during a
completed UTC hour and create an in-app notification when the count reaches a
configured threshold. The interesting part is not counting rows. It is making
the result remain correct when a process crashes, two worker cycles overlap, a
rule changes, or a user reads the notification.

## Start with one exact example

At `2026-09-08T10:15:00Z`, an editor creates a rule named `Signup volume` for
event `signup`, threshold `3`, enabled. Its first eligible start is 11:00. The
worker cannot evaluate that window until 12:00 because `[11:00,12:00)` must be
complete. If it sees events at 11:00, 11:27, 11:59:59, and 12:00, it records a
count of three. The 12:00 event belongs to the next window.

One transaction advances the rule cursor to 12:00, inserts an evaluation, and
inserts a notification. If the transaction fails, none of those facts exist.
If it commits and the process dies before returning from the method, the next
process sees the cursor at 12:00 and does not repeat the 11:00 window.

An edit made exactly at 10:00 may evaluate `[10:00,11:00)` after 11:00. An edit
at 10:00:00.001 begins with `[11:00,12:00)`. This is why the rule stores the
first eligible window instead of reconstructing intent later from `UpdatedAt`.

## Four records answer four questions

```text
AlertRule
  configuration + current revision + next window cursor
       |
       | one immutable attempt per revision/window
       v
AlertEvaluation
  observed count + threshold + triggered + evaluated time
       |
       | zero or one, only when triggered
       v
ProjectNotification
  immutable facts shown in the project inbox
       |
       | zero or one per user
       v
NotificationRead
  this user read this notification at this time
```

“Evaluated” means the database count ran and evidence was committed.
“Triggered” is the comparison `observedCount >= threshold` stored on that
evidence. “Notification created” means the triggering evidence produced its
single inbox item in the same commit. “Read” belongs to a particular user and
can happen independently for every member.

Keeping these concepts separate answers useful questions. A below-threshold
hour has an evaluation but no notification. An unread notification has still
been delivered to the inbox. Two members can see the same immutable notification
while only one has a `NotificationRead` row.

## The durable timeline

For a due rule, `HourlyAlertService` performs this sequence:

1. Capture UTC time and calculate the last completed hour boundary.
2. Select the oldest due rule cursor, with a cycle budget of ten windows.
3. Begin a database transaction and reload the rule at the expected revision.
4. Inside that transaction, read `ProjectIngestionState`; stop if paused.
5. Move history older than 24 hours to `SkippedThrough` and place the cursor at
   the oldest permitted start.
6. Count matching project events with `timestamp >= start && timestamp < end`.
7. Add the immutable evaluation and, when triggered, its notification snapshot.
8. Advance `NextWindowStart` by one hour.
9. Save and commit all records together.

The current revision check prevents an old worker decision from being applied
to a newly edited rule. The unique `(RuleId, RuleRevision, WindowEnd)` index is
the final duplicate barrier for evaluation. The unique notification evaluation
index prevents a second inbox item even if application coordination is wrong.
The cursor makes recovery cheap because the worker does not need an in-memory
acknowledgement to know what committed.

SQLite permits limited concurrent writing. The transaction and uniqueness
constraints remain the source of truth; a worker cycle that encounters database
contention logs the failure and retries from durable state on a later sweep.

## Why revision changes reset time

Editing a rule increments `Revision` and saves a new first eligible window. An
old revision might mean “signup >= 10”; the new revision might mean “purchase >=
2.” Reusing the old cursor would mix two configurations and could evaluate an
hour that was already partly complete when the new configuration appeared.

Disabling and deleting are also conditional on the revision supplied by the
editor. Delete is soft: it sets deleted and disabled while preserving previous
evaluations and notification facts. A stale edit receives conflict instead of
silently overwriting a later edit.

Rule creation and edits run inside maintenance-aware transactions. The worker
does its own pause check inside its transaction because it has no HTTP request
guard. This matters during project erasure or another operation that requires
all project writers to stop.

## Catch-up is a work policy, not missing evidence

Suppose a cursor is seven days old when the service resumes. Creating 168
evaluation rows would delay current work and could flood an inbox. SR-20 permits
only the last 24 hours. The service stores `SkippedThrough` and advances to the
oldest allowed start before evaluating.

Skipped history is not reported as evaluated. The watermark says the scheduler
intentionally omitted those windows under its catch-up policy. The next ten due
windows are processed in time order, and later cycles continue from the stored
cursor. This bounds each cycle without pretending the job is fully caught up.

The ten-window budget limits how many hourly count queries one cycle starts.
It does not cap event rows scanned by each count query. Merely returning ten
results after counting every missed hour would not limit evaluation work.

## Inbox security and immutable facts

Every inbox query includes the project id, and membership is checked at request
time. Removing a member immediately removes their ability to list or mark that
project's notifications, even if an old per-user read row remains stored.

The mark-read route has no user id in its request body. It derives the id from
the authenticated principal and uses a unique `(NotificationId, UserId)` key.
`INSERT OR IGNORE` gives repeated and concurrent mark-read requests the same
stored result. It never lets a caller select another member's identity.

Notifications copy only facts needed for the inbox: rule and event names,
revision, window, count, threshold, and evaluation time. They do not copy event
properties. This both minimizes retained data and keeps a past notification
understandable after the rule is renamed or deleted.

The observed count describes rows visible when evaluation actually runs. It is
not an ingestion-time snapshot. An event arriving before evaluation is counted;
an event arriving after evaluation does not reopen the immutable result in v1.

## Walk through the code

`HourlyAlerts.cs` defines the four persistence concepts. `AlertRule` is mutable
and carries scheduler progress. The other three types record facts that are only
inserted.

`HourlyAlertService.FirstEligibleWindowStart` is a pure boundary calculation.
Its two most useful fixtures are 10:15 to 11:00 and exactly 10:00 to 10:00.

`ProcessDueAsync` captures the completed boundary and repeatedly selects the
oldest cursor. It stops after ten committed evaluations. `ProcessNextWindowAsync`
owns the transaction, current-revision check, pause check, half-open count, new
records, and cursor change.

`HourlyAlertEndpoints` owns HTTP validation and authorization. Editors create,
edit, and soft-delete rules. Viewers can list rules and notifications and mark
their own notification read. Lists clamp page size to 1–100 and use stable
secondary ordering by id.

`HourlyAlertWorker` creates a fresh dependency-injection scope each sweep. Its
configuration switch pauses scheduling without deleting rules, progress, or
notifications. The service remains independently callable in deterministic
tests.

## Failure experiments

Run these experiments against a temporary file-backed SQLite database rather
than an in-memory fake.

**Fail before commit.** Add a command interceptor that throws on the final save.
After disposing the failed context, verify there is no evaluation, notification,
or cursor advance. Remove the fault and run again. Exactly one evaluation and,
when triggered, one notification should commit.

**Die after commit.** Let processing commit, then throw from a wrapper before
the caller records any in-memory success. Dispose the host and construct a new
one over the same file. The persisted cursor skips the completed window and the
unique constraints still show one evaluation and notification.

**Race an edit.** Pause one processor after it selects rule revision 4. Commit
an edit to revision 5 and a new eligible cursor. Release the processor. Its
transactional reload for revision 4 fails, so it cannot create revision-4
evidence using revision-5 configuration.

**Race two processors.** Give each a separate context over the same database and
release both at the contested operation. One transaction commits. The other
must either observe advanced progress or lose to the unique evaluation key; a
later retry sees the durable cursor.

**Pause during scheduling.** Set the project pause state before the transactional
check. The cycle creates no evaluation and does not advance progress. Unpausing
allows a later cycle to apply the normal 24-hour catch-up policy.

**Insert late data.** Evaluate `[11:00,12:00)`, then insert another matching event
at 11:30. A later sweep does not change the evaluation or create a notification.
That limitation is explicit v1 behavior.

## Verification checklist

- A threshold-equality fixture creates one notification.
- Below-threshold and empty windows create nontriggering evaluations.
- Events at the start are included; events exactly at the end are excluded.
- Creation at an exact hour and between hours chooses the documented cursor.
- Create and edit reject blank, over-200-character, and out-of-range fields.
- The twenty-first nondeleted project rule is rejected.
- Stale update and delete revisions return conflict.
- Delete preserves evaluation and notification rows.
- A disabled, deleted, paused, or superseded rule cannot create new evidence.
- A cycle evaluates no more than ten windows and records old skips in one
  watermark rather than synthetic evaluation rows.
- Restart and race tests retain one evaluation and at most one notification.
- Inbox pagination is bounded and ordered deterministically.
- Two users have independent read markers; repeated mark-read is idempotent.
- A removed member cannot see the project inbox.
- Notification storage contains no arbitrary event properties.

The focused command after the model configuration and migration are integrated
is:

```powershell
C:/Users/E/.dotnet/dotnet.exe test --filter "FullyQualifiedName~HourlyAlertTests|FullyQualifiedName~ProjectRoleTests|FullyQualifiedName~InsightEndpointsTests"
```

Run migration tests as part of broader verification because these four tables,
their composite keys, and unique indexes are part of the correctness design.

## Limits and rollout

This feature detects configured thresholds; it does not learn a baseline or
perform anomaly detection. It evaluates UTC hours only, uses exact event-name
matching, and never revises an evaluated hour for late arrivals. It has no
email, webhook, Slack, or other external delivery.

Roll out the schema first, then the API, and enable the worker after exercising
disabled rules. `HourlyAlerts:Enabled=false` pauses worker sweeps while retaining
all durable state. On resume, the same 24-hour catch-up bound applies. Disabling
the feature is operationally different from deleting a rule, rolling back the
application, or restoring a database backup.

The invariant to teach back is: for each rule revision and completed window,
there is at most one immutable evaluation, at most one notification derived
from that evaluation, and one independent read marker per user; configuration,
progress, evidence, and delivery change together wherever they must.
