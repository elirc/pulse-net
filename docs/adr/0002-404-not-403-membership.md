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
