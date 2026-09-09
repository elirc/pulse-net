# Recovering and cancelling exports

Use an isolated practice database for the experiments in
[bootcamp session 18](../../astradocs/bootcamp/18-recoverable-exports.md).
Current implementation/test status is in the [verification record](../../astradocs/bootcamp/verification.md).

1. Stop old worker versions and apply the explicit database upgrade workflow.
   The resumable-export migration leaves existing completed documents intact;
   existing Running jobs become unowned recoverable work.
2. Set `Exports:ClaimsEnabled=false` to pause new work while inspecting jobs.
   This does not revoke an already running attempt. Cancel a specific job to
   stop that work durably. `Exports:AcceptSnapshots=false` rejects new snapshot
   requests with 503 while leaving existing completed downloads available.
3. Use project export history and job status to inspect state, attempt generation,
   last heartbeat, consistency, and snapshot capture time. Running work can be
   recovered at lease expiry. Do not reset every Running job to Pending or
   clear generations; older workers may still be alive.
4. An Editor can POST `/api/projects/P/exports/J/cancel`. Pending becomes
   Cancelled (200); Running becomes CancelRequested (202). Repeat requests are
   successful. Completed/Failed returns 409. A live owner observes cancellation;
   an expired owner is handled by the recovery sweep.
5. Cancelled jobs cannot be downloaded. Terminal deletion also removes copied
   snapshot rows. History is metadata only; integrity hashes the exact UTF-8
   download bytes of completed stored documents.

For a fixed event export, POST `/api/projects/P/exports` with this body:

```json
{
  "type": "events",
  "format": "json",
  "consistency": "snapshot",
  "event": "pageview",
  "from": "2026-03-01T00:00:00Z",
  "to": "2026-03-02T00:00:00Z"
}
```

The interval is inclusive and at most 90 days. Property filters are unsupported.
The worker chooses the capture view; submission time is not the snapshot time.
The 10,000-row and 16-MiB copied-input caps fail as
`snapshot_row_limit_exceeded` or `snapshot_byte_limit_exceeded`. Narrow the range
or event name and create a new request. There is no partial successful snapshot.

Recovery of a job with a ready snapshot reuses it. Retrying a Failed job through
the retry endpoint creates another job, with fresh input and a new ID. Record
both IDs when diagnosing differences. A capture timestamp is evidence of the
view used, not evidence of the time every event originally occurred.

Measure snapshot capture duration and SQLite contention with representative
payload sizes before increasing workload. The application still renders bounded
documents in memory and stores them inline. More worker instances do not remove
SQLite's single-writer constraint.
