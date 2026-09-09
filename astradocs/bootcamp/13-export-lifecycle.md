# Session 13: a job, its history, and its exact bytes

Stories: MID-23 through MID-26. Follow the [ledger](stories.json) for actual
verification status; the examples below describe the implementation contract.

## Retry means another attempt with its own identity

An export fails on Monday. On Tuesday you fix the cause and click retry. Should
Monday's row become pending again, losing its error and completion time?

The new retry feature creates a new job. The original remains failed and keeps
its evidence. The new row copies only Type, Format, and the exact ParamsJson,
gets a new ID and timestamp, and starts with empty output/error fields. Repeated
retry requests deliberately create separate attempts; this feature has no
idempotency key or permanent parent relationship.

This resembles taking an exam again: a new attempt can have a new result without
rewriting the earlier attempt's grade. In database terms, insert a new job instead
of mutating the old job's state back to Pending. In HTTP terms, return 202 with
the source ID and a Location for the new job.

Read [ExportJobOperationsService](../../src/Pulse.Infrastructure/Services/ExportJobOperationsService.cs).
`RetryAsync` reads a project-scoped source, requires Failed, constructs the new
entity, and calls the same enqueue helper used by ordinary export creation.
The helper saves before ringing `ExportSignal`. A signal is a wake-up hint;
the durable row is the work the worker must discover.

A retry uses current source data and current saved insight configuration. The
same parameters do not recreate a historical database state. If an insight is
still missing, the retry can correctly fail again. Tests should follow the new
job's lifecycle rather than assume its first subsequent GET still says pending.

## History should not download documents internally

The history endpoint returns job metadata: ID, type, format, status, row count,
error, and times. An ExportJob entity also contains ParamsJson and ResultContent.
Loading whole entities and then returning a small DTO still retrieves the large
document from storage. A small HTTP response does not prove a small SQL query.

The service uses an expression that selects only metadata fields before
materialization. [ExportMetadataQueryTests](../../tests/Pulse.Tests/Infrastructure/ExportMetadataQueryTests.cs)
inspects the SQL and asserts that neither document content nor parameters are
selected. This is a focused performance assertion tied to an actual feature
promise, not a brittle count of every unrelated SQL query in an HTTP request.

History uses descending creation time and ID, with a versioned cursor bound to
the project and normalized status/type filters. This repeats session 9's tied
timestamp problem with jobs rather than events. Filtering happens before paging;
the extra `limit+1` row determines whether another page exists.

The list is live. A job can change status between pages, moving into or out of
a filtered result. A new job appears on a restarted first page. The cursor is
a position, not a snapshot of list membership.

## A delete condition belongs in the DELETE

Pending and Running jobs are not eligible for deletion. Completed and Failed
jobs are. The database statement includes the terminal-state predicate together
with project and job ID. There is no unguarded gap between “I saw Completed”
and “delete whatever row now has this ID.”

If the conditional delete affects zero rows, a scoped existence query distinguishes
404 from a currently nonterminal conflict. Another request can remove the row
between observations; returning 404 for a disappeared resource is reasonable.

Deleting a job removes access to its inline document through future status,
download, and integrity requests. It does not remove source events or recall a
download that already loaded or transmitted bytes. It also does not promise
secure physical erasure or immediate shrinking of the SQLite file.

This repeats session 11's conditional rotation lesson: an invariant is strongest
when it is part of the actual database mutation, rather than only a prior C# `if`.

## Integrity is about bytes, not appearance

Consider two JSON strings with the same meaning but different spaces or property
order. They are not the same document bytes. The integrity endpoint hashes the
stored string's exact UTF-8 bytes without a BOM; it does not parse and reserialize
the JSON or normalize line endings.

| Document feature | What the digest includes |
| --- | --- |
| Spaces and indentation | Exact stored bytes |
| CSV quotes and delimiters | Exact stored bytes |
| CRLF versus LF | Their different byte sequences |
| Unicode characters | Their UTF-8 encoding |
| HTTP compression/framing | Excluded; the digest describes decoded document bytes |

`ExportDocument.Bytes` is shared by download and integrity, preventing their
encodings from drifting. The integrity response contains job ID, served content
type, byte length, lowercase SHA-256, and `encoding:"utf-8"`.

An empty stored string is a valid zero-byte document with a well-defined hash.
Null content on a Completed job is inconsistent state and returns 409. Inventing
an empty document for null would conceal that storage problem.

[ExportLifecycleTests](../../tests/Pulse.Tests/Api/ExportLifecycleTests.cs) downloads
ASCII CSV, Unicode JSON, an empty document, and explicit CRLF content. The test
computes a digest independently over the HTTP response bytes and compares it
with integrity metadata. Testing only the shared helper against itself would
not verify what the endpoint actually served.

Encoding and hashing allocate/work in proportion to document size. Inline export
storage already has that architectural cost; a checksum endpoint does not make
large documents streaming or cheap. A future storage redesign must preserve the
same byte-level contract if clients depend on its digests.

## Practice ladder

1. Draw the original failed row and a new retry row. Mark exactly which fields
   copy and which start fresh. Explain why repeated retry clicks make more rows.
2. Follow the new job's Location until it is terminal. Describe why 202 is
   acceptance, not a completed downloadable result.
3. Locate the metadata projection and compare it with the full entity. Explain
   why mapping after `ToListAsync` would lose the intended database benefit.
4. Walk tied creation timestamps with page size one, then change the status
   filter and explain why the old cursor is rejected.
5. Find the status predicate in the actual delete statement. Sketch a worker
   transition between a previous read and that statement.
6. Count characters and UTF-8 bytes for a string containing `é`. Explain why
   character count cannot be used as the downloadable byte length.
7. Explain why a matching checksum does not prove an export contains every
   event the analyst wanted; it proves agreement about the served document bytes.

Teach back tomorrow: distinguish source data, job input, job metadata, and stored
output. Each is a different object with a different lifetime and correctness
promise, even though one screen may display all four.
