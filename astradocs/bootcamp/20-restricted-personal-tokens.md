# Session 20: a token can grant less than its owner has

Story SR-16, building on roles and audit attribution. The implementation is
being verified; check [the evidence record](verification.md) before treating
an example as an executed acceptance test.

## Begin with a script, a user, and two projects

Ada is an Admin of projects A and B. Her reporting script needs to read analytics
in A. Giving the script Ada's unrestricted personal key also gives it access
to B and administrative work it does not need.

A restricted token records allowed projects, named scopes, and expiry. It still
authenticates as Ada. It does not become a new independent project member.
Its effective permission is the intersection of four conditions:

```text
current user role
AND allowed project
AND required named scope
AND valid, unexpired, unrevoked credential
```

Read that again as a checklist the server performs, not a checklist the caller
promises to honor. Every condition must be true. One broad condition cannot
compensate for a failed narrower condition.

## A concrete permission table

| User's current role | Token grants | Requested action | Result |
| --- | --- | --- | --- |
| Admin in A/B | A, configuration:read | List flags in A | Allowed |
| Admin in A/B | A, configuration:read | List flags in B | 404 |
| Admin in A | A, configuration:read | Create a flag in A | 403 |
| Viewer in A | A, configuration:write | Create a flag in A | 403 |
| Admin in A | A, all four scopes | Edit members in A | 403 |
| Admin in A | A, analytics:read, expired | Read persons in A | 401 |

An invisible project produces 404. An authenticated caller lacking a capability
on an allowed project receives 403. An expired credential does not authenticate
and receives 401. These responses communicate different failed conditions.

## Follow the request through real files

1. [PersonalApiKey](../../src/Pulse.Domain/Entities/PersonalApiKey.cs) stores mode,
   expiry, hash, and suffix. Mapping tables store projects and scopes.
2. [AuthEndpoints](../../src/Pulse.Api/Endpoints/AuthEndpoints.cs) validates a
   requested restriction and displays the plaintext key exactly once.
3. [PersonalApiKeyAuthenticationHandler](../../src/Pulse.Api/Auth/PersonalApiKeyAuthenticationHandler.cs)
   hashes the presented key, checks expiry, and adds restriction claims.
4. [RestrictedToken](../../src/Pulse.Api/Auth/RestrictedToken.cs) rejects global
   or unclassified operations and checks project allowance and named scope.
5. [ProjectAccessService](../../src/Pulse.Api/Auth/ProjectAccessService.cs) checks
   current membership and role using the shared route matrix.
6. [ProjectEndpoints](../../src/Pulse.Api/Endpoints/ProjectEndpoints.cs) filters
   project lists and removes project credentials from restricted responses.

On a first pass, trace the happy path. On a second pass, remove A from the allowed
projects. On a third pass, leave the token unchanged but demote Ada to Viewer.
The changed outcome should come from a different layer each time.

## Scopes describe capabilities, not HTTP verbs

The four v1 names are `analytics:read`, `configuration:read`,
`configuration:write`, and `exports:write`. They do not imply one another.
An export writer needs analytics:read as well if the script should poll status
or download output. Configuration:write does not silently include read access.

A POST can be a read operation: an insight preview accepts a JSON request body
but does not save configuration. A flag explanation is also a POST read.
The [permission matrix](../../src/Pulse.Api/Auth/ProjectPermissionMatrix.cs)
classifies exact routes instead of inferring authority from GET versus POST.

Administrative routes have no restricted-token scope. All four scopes together
still cannot manage members, project credentials, audit administration,
retention, erasure, or personal tokens. The global exceptions are the caller's
own `/api/auth/me` and a filtered GET `/api/projects`. Other global or unknown
routes fail closed for restricted tokens until deliberately classified.

## Stop the replacement-key escape

Imagine protecting only the new restricted-key route. A script could call the
old personal-key endpoint, mint an unrestricted key, and ignore its restrictions.
Both creation paths, listing, and revocation therefore require an actual JWT
session. The route group authenticates the JWT scheme explicitly.

Legacy unrestricted keys keep their management capabilities and unlimited
lifetime after migration, but they cannot manage personal keys. That is an
intentional control-plane change: interactive JWT sessions manage credentials;
automation uses the capabilities of the key it was issued.

Explain this using a building pass. A room-limited visitor pass must not authorize
printing an all-access replacement pass. The printing desk has a separate
credential requirement. The same idea applies to an old API route as much as
to a new one.

## Redaction is part of authorization

Suppose a restricted token can GET project A and the response includes A's read
key. The script can then use that broader key outside its named scopes. Even
correct endpoint checks would be undermined by the response.

Restricted project lists and details omit write/read credentials regardless of
Ada's Admin role. Filtering the list alone is insufficient. Role-based redaction
alone is also insufficient. The response considers both role and token mode.

Creation returns `Cache-Control: no-store`. The database stores the key's SHA-256
hash and display suffix, never the plaintext. Audit entries keep the personal
key's database ID for attribution without recording the secret token string.
Listing later shows safe metadata such as mode and expiry.

## Time and revocation are live conditions

Creation accepts 1–20 project IDs, 1–4 named scopes, a trimmed name of 1–200
characters, and an expiry from one minute through 90 days ahead. Every requested
project must have a current membership. Duplicate mapping inputs are normalized
before insertion; an unknown project rejects the entire request.

Authentication treats `expiresAt == now` as expired. The test clock advances
from one tick before expiry to equality instead of sleeping. That isolates the
boundary from scheduler delays and machine load.

Revocation deletes the key and its mappings transactionally. Subsequent requests
cannot authenticate the old string. Membership/role queries also run on use;
a token's claims do not freeze its owner's earlier role for the token's lifetime.

## Reproduce the codebase experiments

```powershell
dotnet test --filter FullyQualifiedName~ScopedPersonalTokenTests
dotnet test --filter FullyQualifiedName~ScopedTokenUpgradeTests
dotnet test --filter FullyQualifiedName~AuthzMatrixTests
```

The scope tests use a user with A/B membership, then issue a token for A.
They verify project filtering and redaction, write permission, current-role
demotion, membership removal, exact expiry, and revocation. They also iterate
the route matrix: every disallowed-project route must remain invisible, and
every administrative route must reject even a token carrying all named scopes.

Predict what happens if a newly added administrative route accidentally receives
configuration:write. The matrix test identifies its classification, but a test
that only derives expectations from the same mistaken matrix may not catch the
product-policy error. Keep explicit member/key/retention/erasure negative examples
as well. A useful test challenges the policy; it does not merely echo the code.

For a manual practice run, create a JWT session in an isolated database, create
two projects, and POST `/api/personal-api-keys/restricted` for only the first.
Use a fresh client with only the returned personal token. List projects, inspect
project details, list flags, try to create a flag, and try the old token-creation
route. Record both the status code and which failed condition explains it.

## Review questions at increasing depth

Start: what is the difference between authentication and authorization here?
Authentication identifies the valid credential and user; authorization limits
what that user and credential may do on this request.

Next: why does a token with configuration:write fail after its owner becomes a
Viewer? Named scope is one necessary condition. Current role is another.

Deeper: why must old application instances stop serving restricted tokens during
rollout? An old handler can authenticate the same key hash while ignoring its
new restriction metadata. Schema support alone does not enforce policy.

Finally: explain how a project response can accidentally undo careful scope
checks. Point to the redaction code and the test containing the actual project
keys as markers that must be absent from the response.

Write your own four-condition equation, then explain it to someone without
using the word "intersection." Revisit this lesson when adding alerts and
erasure: new routes must fit the policy rather than bypassing it.
