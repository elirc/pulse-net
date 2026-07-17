# ADR 0007: Exports paginate with (timestamp, id) cursors, not limit/offset

**Status:** accepted

## Context

Management list endpoints (insights, persons, flags…) use `limit`/`offset` —
fine for small, human-paged lists. Exports are different: they walk the
entire event or person table *while ingestion keeps appending*. With offset
pagination a row inserted before the client's current position shifts every
subsequent page, so the export sees duplicates or silently skips rows —
corrupting exactly the workload (backfills, warehouse syncs) exports exist
for. Offset scans also get linearly slower as the offset grows.

## Decision

`GET /export/events` and `GET /export/persons` return an opaque cursor:
base64 of `"{UtcTicks}:{Guid:N}"` of the last row scanned. Pages are ordered
by `(Timestamp, Id)` — a total order, since the Guid tiebreaks equal
timestamps — and the next page resumes with

```sql
WHERE Timestamp > @afterTicks
   OR (Timestamp = @afterTicks AND Id > @afterId)
```

The cursor rides in the JSON body (`nextCursor`) and the `X-Next-Cursor`
header (so CSV responses can paginate too); `null` means done. A malformed
cursor is a 400, never a silent restart. The async export job processor
drives the same paging loop internally (1000-row pages, 50,000-row cap).

Property filters are applied *after* the SQL page scan (properties are opaque
JSON to SQLite), so a filtered page may contain fewer than `limit` rows while
the cursor still advances — the cursor tracks scan position, not result
count.

## Consequences

- Pages are stable under concurrent appends: no duplicates, no skips of
  pre-existing rows (new rows sort after the cursor and appear in later
  pages). This is pinned by a test that inserts mid-scan.
- Resume position is a keyed index seek, so page N costs the same as page 1.
- Clients cannot jump to an arbitrary page — inherent to cursors and
  irrelevant for exports.
- Filtered pages of size < limit (possibly 0) with a non-null cursor surprise
  naive clients; documented, and the alternative (scan until the page fills)
  would make worst-case latency unbounded.
- The scheme depends on rows only ever *appending* after the cursor position.
  Backdated events (a client-supplied older timestamp arriving mid-export)
  can land behind an already-passed cursor and be missed — accepted, matching
  how warehouse syncs treat late data.
