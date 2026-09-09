# Preview, enable, observe, and disable event retention

Retention removes processed Events only. It does not remove people, aliases,
cohort definitions/memberships, queued work, dead letters, exports, snapshots,
or backup copies. An old event arriving later may remain until a subsequent
sweep. Use [session 19](../../astradocs/bootcamp/19-event-retention.md) for the
transaction and boundary exercises.

1. Apply the retention migration with workers stopped. Every migrated policy is
   disabled; existing events remain. New API and demo projects also start disabled.
2. As an Admin, GET `/api/projects/P/retention`. Record revision, enabled, and days.
   GET `/api/projects/P/retention/preview` to observe a UTC cutoff and count.
   Preview does not reserve a future row count or initiate deletion.
3. PUT `/api/projects/P/retention` with `enabled`, `days` (30–3650), and
   `expectedRevision` from the GET response. For example:

   ```json
   { "enabled": true, "days": 365, "expectedRevision": 1 }
   ```

4. Observe GET `/api/projects/P/retention/runs`, which returns the latest 50 runs.
   Each run preserves its own cutoff/revision, removed count, batch count, and
   timestamps. The worker sweeps once per minute, at most 20 projects and one
   batch of at most 1000 IDs per project. A busy database can delay this schedule.
5. To disable the policy, read its latest revision and PUT `enabled:false` with
   the chosen days and current expectedRevision. A stale revision returns 412;
   resolve the settings conflict before retrying. Policy changes supersede an
   active run. Previously committed deletion cannot be undone by increasing days.
6. Set `Retention:WorkerEnabled=false` to pause new worker batches operationally.
   This does not roll back a transaction already in progress. Resume with the
   same setting true; unfinished runs keep their original cutoff.

Eligibility is `Event.Timestamp < run.Cutoff` within the project. An event exactly
at cutoff survives. The batch updates receipt pointers, deletes events, and
advances progress in one transaction. A rollback leaves all three unchanged.
Receipts remain Processed and report `retiredReason:"retention"`; their successful
processing time and replay generation remain truthful.

After a crash, inspect the stored run instead of estimating progress from logs.
Do not manually advance counters or rewrite cutoffs. A future run can collect
late old events. Recovering deleted data requires a separate recovery source
and a plan for data written since that source was captured.

Current build and test evidence is recorded in
[the bootcamp verification file](../../astradocs/bootcamp/verification.md).
