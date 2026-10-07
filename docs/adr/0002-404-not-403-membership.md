# ADR 0002: Non-members get 404, never 403

**Status:** accepted

## Context

Every management endpoint is scoped to a project by a GUID in the route. An
authenticated user who is not a member of that project must be denied — but
*how* they are denied leaks information. A 403 confirms the project id
exists; project ids appear in URLs, logs and support tickets, so an attacker
enumerating ids could map out which projects are real.

## Decision

`ProjectAccessService.RequireMemberAsync` returns:

- **401** when the caller is not authenticated at all,
- **404** when the caller is authenticated but not a member — the same
  response an entirely nonexistent project id produces.

There is no 403 anywhere in the project-scoped API. The rule applies
uniformly to every endpoint class (queries, persons, cohorts, flags,
dashboards, annotations, exports, dead letters), which the test suite pins
with a parameterized matrix over 20 representative routes.

## Consequences

- Project existence is unobservable to non-members; enumeration yields
  nothing.
- Legitimate users who lose access see "not found" rather than "forbidden" —
  slightly less self-diagnosing, an accepted trade-off (the project list
  endpoint shows what they *can* see).
- Handlers must check membership before any other validation so that error
  shapes don't differ between "no such project" and "not your project".
- Uniformity is the hard part: one endpoint answering 403 would re-open the
  leak, so the authz matrix test exists to make regressions loud.

## In the code

- The rule: `ProjectAccessService.RequireMemberAsync`
  (`src/Pulse.Api/Auth/ProjectAccessService.cs:36-51`) — a 401 problem when
  no user id can be read from the principal (lines 38-45), otherwise one
  `AnyAsync` over `ProjectMemberships` and `Results.NotFound()` on a miss
  (lines 47-50).
- The read-key variant `RequireReadAsync` (line 58 onward) accepts the
  project's `rk_live_` key via `X-Api-Key` only when the caller is *not*
  otherwise authenticated (line 61); an authenticated caller falls back to
  membership.
- Call sites: `RequireMemberAsync` appears 44 times across
  `src/Pulse.Api/Endpoints/*.cs`; searching `src/Pulse.Api` for `403` or
  `Forbid` finds only the explanatory comment at `ProjectAccessService.cs:11`.
- The matrix: `tests/Pulse.Tests/Api/AuthzMatrixTests.cs` — a
  `TheoryData<string, string, string?>` of 20 `(method, path, body)` rows
  (from line 28) driving two `[MemberData]` theories (lines 54 and 69), so
  40 executed cases.

## Review notes

- The guarantee rests on handler discipline: each endpoint must call the
  guard before touching project data, and a new endpoint not added to the
  matrix would not be caught. An endpoint filter on the
  `/api/projects/{projectId}` route group would make the rule structural.

**Check:** pick any endpoint in `src/Pulse.Api/Endpoints/` and confirm its
first database access happens *after* the `RequireMemberAsync` /
`RequireReadAsync` early return.
