# ADR 0010: durable worker ownership and fixed export input

Status: accepted for the bootcamp implementation; rollout requires the
[verification gates](../../astradocs/bootcamp/verification.md).

This supersedes ADR 0001's single-worker and retry-attempt assumptions while
preserving its durable admission decision. ADR 0009's atomic event effects,
queue acknowledgement, and replay transitions remain required. ADR 0007's
cursor pagination remains appropriate for live exports; snapshot mode adds a
separate immutable-input contract.

Public capture may attach a project-scoped client event UUID. A seven-day
admission-key lifetime bounds retry deduplication. Processing identities and
receipt positions have separate lifetimes. Admission, replay, and capacity
edits serialize through a durable per-project gate. The queue is authoritative;
in-memory signals only reduce wake-up latency.

Ingestion owners hold per-project leases with increasing generations. Every
success, retry, and dead-letter transaction fences the token before effects.
Recognized SQLite contention uses durable one/two/four/eight-second backoff;
the fifth failed transient attempt dead-letters. Unknown storage failure and
host cancellation leave the obligation intact without inventing an attempt.
Malformed envelopes are permanent validation failures.

Exports use per-job ownership, 30-second leases, and conditional publication.
Cancellation is persisted; heartbeats or abandoned-lease recovery finalize it.
Only a current Running owner can publish. Host cancellation is not Failed.
Live mode retains its existing rendering and row-cap behavior.

Snapshot mode captures complete event-export values inside a bounded transaction,
including readiness and capture timestamp. It permits up to 10,000 rows and
16 MiB of serialized input over an explicit inclusive range of at most 90 days.
It supports event/date selection, not property filters. Cap breaches fail
explicitly. Recovery renders the same ready copy; a new retry job captures anew.
Cancellation/deletion remove copies with the corresponding job transition.

Consequences: additional tables and short write transactions make lifetimes and
ownership explicit. SQLite still serializes writers; multiple workers do not
promise linear throughput. Snapshot capture temporarily holds a write transaction
and duplicates data. Later erasure must inventory those copies. Stop unfenced
worker versions before enabling multiple instances; a mixed fleet cannot enforce
the invariant. Keep measured test results separate from throughput claims.
