# Issue and revoke restricted personal tokens

Read [bootcamp session 20](../../astradocs/bootcamp/20-restricted-personal-tokens.md)
for the request path, permission examples, and test exercises. Current verification
is recorded separately in the [bootcamp ledger](../../astradocs/bootcamp/stories.json).

With a JWT session, POST `/api/personal-api-keys/restricted`:

```json
{
  "name": "Project A reports",
  "projectIds": ["00000000-0000-0000-0000-000000000001"],
  "scopes": ["analytics:read"],
  "expiresAt": "2026-09-09T12:00:00Z"
}
```

Use real member project IDs and an expiry one minute to 90 days ahead of the
server's current clock; the timestamp above is illustrative. The response shows
the plaintext once and uses no-store. Only the hash and suffix are persisted.
Use the returned key as a Bearer token in the script.

Choose among analytics:read, configuration:read, configuration:write, and
exports:write. Scopes do not imply one another. A script that creates exports
and then downloads them needs exports:write and analytics:read. The route matrix
classifies POST previews as reads where appropriate.

All token-management routes require a JWT, including the original unrestricted
creation endpoint. A personal token cannot create a broader replacement or
manage existing tokens. JWT-authenticated DELETE `/api/personal-api-keys/K`
revokes the key and its mappings. The old string fails on its next use.

Current membership and role remain required on every project operation.
Removing a membership removes visibility; demotion removes higher-role actions.
Expiry equality is expired. Restricted tokens see only allowed projects in the
project list and never receive project read/write credentials, even when their
user is an Admin. Administrative member, credential, audit, retention, and
erasure operations remain unavailable to restricted tokens.

Deploy the schema and all scope-aware application instances before issuing
restricted keys. Old instances can recognize a stored key hash while ignoring
its restrictions. Existing keys migrate to explicit legacyUnrestricted mode
without an expiry or capability loss, except that token management now requires
JWT. Never convert a restricted key to legacy mode as a workaround; revoke it
and issue the intended credential through the supported session workflow.
