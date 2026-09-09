# HTTP contracts and project isolation

**Outcome:** design a small API change that behaves predictably for callers
and cannot operate on another project's records.

## Begin with caller-visible behavior

For replay, the caller supplies a project ID and a dead-letter ID. A member
gets `202` if the stored event is queued. Missing credentials produce `401`.
A signed-in non-member gets `404`. Invalid stored data produces `422` and
remains available for inspection. Repeating a successful replay gets `404`
because the original letter has been consumed.

These outcomes are the contract. The method name or internal class layout
is an implementation choice. Write the contract before opening a service.

## Authentication versus authorization

Authentication establishes the caller's identity. Authorization checks what
that identity may do. A valid JWT alone cannot grant access to every project.

Trace `RequireMemberAsync` in
[ProjectAccessService](../../src/Pulse.Api/Auth/ProjectAccessService.cs).
Then trace the project predicate in `ReplayAsync`. Both matter: checking
membership for project A and looking up a letter by ID alone would allow a
member of A to operate on a known letter from project B.

The read key supports specified query operations. It does not grant replay,
even though replay eventually adds an event. Credential permissions describe
caller capabilities, not which database table a method happens to write.

## Write a behavior matrix

| Caller / input | Project metrics | Replay |
| --- | --- | --- |
| No credentials | 401 | 401 |
| Project read or write key only | 401 | 401 |
| Member with JWT or personal token | 200 | 202 if valid letter exists |
| Authenticated non-member | 404 | 404 |
| Member of both A and B using B's letter under A | A's counts only | 404 |
| Member with malformed stored letter | Unchanged counts | 422; preserve letter |

Read [IngestionOperationsTests](../../tests/Pulse.Tests/Api/IngestionOperationsTests.cs)
and map each test to a row. Identify which credential variant is inferred
from shared authorization code and which is explicitly tested; a useful
extension is a personal-token test covering revocation.

## Exercise: add a bounded list filter

Add an optional `errorContains` filter to dead-letter inspection. Start by
specifying empty input, maximum length, case behavior, and authorization.
Use LINQ parameters rather than constructing SQL strings. Keep `limit`
bounded and preserve the existing response shape when the filter is absent.

**Acceptance:** matching and nonmatching rows, omitted filter compatibility,
invalid length behavior, and other-project isolation have tests.

**Review questions:** Can this request mutate anything? Is the error helpful
without exposing another tenant? Does returning `202` claim more than the
system has durably done? Is a retry safe at the HTTP boundary?
