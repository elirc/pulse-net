# 20 mid-to-senior feature stories with follow-along implementation plans

**Status: original implementation plans; implementation is now underway.**
Use the [bootcamp ledger](bootcamp/stories.json) and [journal](bootcamp/journal.md)
for current implementation and verification status. Statements below about
starting behavior describe the pre-implementation codebase.
The linked files are current starting points. New routes, tables, classes,
configuration switches, and commands described below are proposed additions.
The [junior backlog](18-junior-user-stories.md) and
[midlevel backlog](19-midlevel-feature-user-stories.md) are also plans, not
dependencies you can assume are already present.

These stories practice owning a feature beyond its happy-path endpoint:
preserving data across upgrades, defining permissions, handling competing
writes, recovering after a crash, bounding resource usage, and explaining
what the system does and does not guarantee. The work is more demanding than
the earlier backlogs, but the implementation order is deliberately explicit.
Follow one step, demonstrate its checkpoint, and then continue.

## Pick a feature

| ID | New capability | Main engineering practice | Prerequisites in this file |
| --- | --- | --- | --- |
| [SR-01](#sr-01) | Upgrade an existing database without losing data | Migration adoption and recovery | None |
| [SR-02](#sr-02) | Give project members viewer, editor, or admin access | Permission matrices and invariants | SR-01 |
| [SR-03](#sr-03) | Browse a trustworthy management audit trail | Atomic evidence and data minimization | SR-01, SR-02 |
| [SR-04](#sr-04) | Detect competing feature-flag edits | Optimistic concurrency and HTTP contracts | SR-01, SR-02 |
| [SR-05](#sr-05) | Inspect and restore a flag's configuration history | Append-only versions and safe restoration | SR-04 |
| [SR-06](#sr-06) | Schedule a future flag rollout change | Durable scheduling and conflict policy | SR-04 |
| [SR-07](#sr-07) | Retry capture submissions without duplicate admission | Idempotency and transactional deduplication | SR-01 |
| [SR-08](#sr-08) | Follow a capture receipt through processing | Durable state and recovery lineage | SR-01; integrate SR-07 if present |
| [SR-09](#sr-09) | Limit a project's pending ingestion backlog | Transactional admission control | SR-01, SR-02 |
| [SR-10](#sr-10) | Retry transient ingestion failures with backoff | Error classification and durable time | SR-01 |
| [SR-11](#sr-11) | Run two ingestion workers with project ownership | Leases, fencing, and concurrency tests | SR-01; integrate SR-10 if present |
| [SR-12](#sr-12) | Cancel and recover asynchronous exports | State machines and worker ownership | SR-01, SR-02 |
| [SR-13](#sr-13) | Export a fixed snapshot of event data | Snapshot semantics and resumable input | SR-12 |
| [SR-14](#sr-14) | Retain events for a configured number of days | Bounded cleanup and irreversible changes | SR-01, SR-02 |
| [SR-15](#sr-15) | Erase a person and block known identities from returning | Cross-feature lifecycle invariants | SR-01, SR-02 |
| [SR-16](#sr-16) | Issue project-scoped, expiring personal tokens | Least privilege and capability intersections | SR-01, SR-02 |
| [SR-17](#sr-17) | Trace one capture through the background worker | Cross-request diagnostic context | SR-01 |
| [SR-18](#sr-18) | Run trends with explicit work budgets | Streaming aggregation and cancellation | None |
| [SR-19](#sr-19) | Explore activity sessions for a person | Time-window semantics and derived data | SR-18's budget helper |
| [SR-20](#sr-20) | Receive in-app alerts for event-count thresholds | Scheduled evaluation and duplicate prevention | SR-01, SR-02 |

SR-01 is an operational feature and the foundation for schema-changing work.
SR-18 is a useful independent starting point if you want to practice analytics
before database upgrades. Other suggested tracks are SR-02 through SR-06 for
management features, and SR-07 through SR-11 for ingestion reliability.
Do not begin by implementing the entire dependency graph at once.

## The implementation routine

1. Run `git status --short` and inspect the relevant existing diff. Preserve
   unrelated work already in the checkout.
2. Read the user story and proposed contract. Write one concrete example in
   your own words, including status code, returned fields, and stored effects.
3. Open the linked files in order. Trace the current path before designing a
   new one: `request -> authorization -> service -> transaction -> worker -> read`.
4. Write down the invariant: a sentence that must remain true after success,
   failure, cancellation, and restart. Examples appear in every story.
5. Follow the numbered steps in small changes. Tests should demonstrate the
   invariant, not just mirror a private method's implementation.
6. Finish the acceptance checks and the story's upgrade/rollout checks.
   Update [the API reference](../docs/api-reference.md), the relevant runbook,
   and an ADR when you introduce a lasting policy or guarantee.
7. Apply [the testing policy](../docs/testing.md). Its full-suite requirement
   for test changes still applies; a focused test run is the first gate.
8. Record what you actually verified and what is still unverified. The earlier
   [validation record](../docs/learning/validation.md) documents an assembly-load
   block and a large-batch timeout; this document does not resolve either one.

Each story names a proposed test class and a focused command. Create that
class under `tests/Pulse.Tests/Api`, `Infrastructure`, or `Domain` as appropriate;
none of those new classes is supplied by this document. Verify test discovery:
zero discovered tests does not mean the feature works. Use
[PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs),
[TestAuth](../tests/Pulse.Tests/TestAuth.cs), and
[TestIngestion](../tests/Pulse.Tests/TestIngestion.cs) for existing patterns.
Use fresh projects/users and real SQLite for persistence behavior.

For races, use two contexts or hosts and an explicit barrier at the contested
operation. For restart tests, retain a temporary SQLite database while disposing
and rebuilding the host. Keep workers under test control when seeding Pending
or Running records; the normal factory runs real workers. Do not use arbitrary
sleeps or repeated retries until a flaky test happens to pass.

## Shared design decisions

- **Project boundaries:** a valid caller still needs scoped resource queries.
  Always include ProjectId when looking up a child resource. Authenticated
  non-members retain the existing 404 behavior. SR-02 introduces 403 for a
  known member who lacks permission within a visible project.
- **Credentials:** project write keys currently authenticate capture and
  `/decide`; read keys grant selected query access; user JWTs and personal
  keys identify a member. Project write keys never become management tokens.
- **Schema changes:** add proposed entities in Domain, mapping/DbSets and
  migrations in Infrastructure, contracts/routes in API, and scoped service
  registrations in [Program](../src/Pulse.Api/Program.cs). No migration exists
  merely because an entity property was added.
- **Transactions:** a signal is a wake-up hint. Commit durable work before
  ringing it. An EF bulk update/delete executes immediately and must share
  the transaction of related writes. After a failed transaction, dispose the
  affected context or explicitly restore its state before reusing it.
- **SQLite scope:** these designs keep SQLite and the current architecture.
  Multiple workers do not imply multiple concurrent database writers or
  unlimited throughput. Contention must be measured and reported.
- **Clocks:** pass a captured UTC time into decision logic. Use controlled
  clocks for tests, explicit UTC boundary fixtures, and cancellation tokens
  throughout database/worker operations. Lease expiry is not sufficient
  ownership proof; SR-11 and SR-12 add ownership tokens.
- **Cross-story integration:** after introducing a central mutation/admission
  service, route every applicable existing and later feature through it.
  Do not bypass versions, audit writes, deduplication, or capacity checks from
  a new endpoint just because its happy path looks simpler.
- **Rollout:** every schema-changing story includes a migration from the last
  supported schema and tests that preserve existing data. Destructive lifecycle
  features start disabled. Feature disablement, application rollback, and
  restoring a database backup are different operations with different effects.
- **Permissions below:** reader/editor/admin mean SR-02 roles where required.
  Stories without SR-02 retain current membership/read-key policy and must
  adopt its route matrix if implemented together. Scopes from SR-16 further
  restrict access; they never expand a user's role.

Microsoft documents that `EnsureCreated` and migrations are not interchangeable
upgrade mechanisms, and SQLite has provider-specific migration limitations.
Read the official [database creation guidance](https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created)
and [SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
when implementing SR-01. The preservation workflow below is a proposed design
for this repository, not a claim that EF supplies a one-command legacy adoption.

## Database and management foundations

<a id="sr-01"></a>
## SR-01 — Upgrade an existing database without losing data

**User story:** As an operator, I want a checked database upgrade workflow so
I can install new features while keeping existing projects and analytics data.

**Current behavior and learning goal:** Startup calls `EnsureCreated`; the
repository has no migration history. Learn how to introduce versioned schema
changes without assuming an existing database was created by migrations.

**Open:** [Program](../src/Pulse.Api/Program.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs),
[Infrastructure project](../src/Pulse.Infrastructure/Pulse.Infrastructure.csproj),
[PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs), and
[getting started](../docs/getting-started.md).

**Proposed contract and invariant:** Add offline application commands `db status`,
`db adopt-legacy`, and `db upgrade`. These are proposed commands, not runnable
today. Status performs read-only inspection and reports database path, schema
state, and pending migration IDs without secrets. Adoption accepts only the
exact supported legacy schema, creates migration history for its verified
baseline, and preserves all data. Unknown/partial schemas fail without changes.
Upgrade applies reviewed migrations. Commands exit nonzero on failure and
never start HTTP or background workers. Existing data is never dropped as a
shortcut. Schema operations require all application writers to be stopped.

**Implementation steps:**

1. Create `DatabaseUpgradeTests` using temporary file-backed SQLite. Produce
   a legacy fixture from the current model before changing it; seed project,
   person, event, flag, queue, and export rows and record their field values.
2. Add a local EF tool manifest and design-time DbContext factory using the
   repository's chosen EF version. Make database selection explicit; tooling
   must not boot workers or silently target a developer's regular database.
3. Scaffold a baseline migration and snapshot representing the current schema.
   Review every table, column, index, converter, default, and uniqueness rule.
   Test that applying this baseline to an empty database reproduces the model.
4. Build a read-only schema inspector. Compare structural SQLite metadata
   against a reference database generated from the baseline, ignoring only
   documented nonsemantic metadata. Refuse extra/missing user objects, partial
   migrations, or incompatible column/index definitions.
5. Implement legacy adoption as a short exclusive maintenance operation:
   verify the schema, create the provider-compatible history table, and record
   only the baseline migration ID/version. Recheck inside the operation so a
   schema change between inspection and adoption cannot be silently accepted.
6. Implement upgrade separately using EF's migration runner and its supported
   transaction/locking behavior. Do not wrap the migration runner in an
   arbitrary application transaction. Report failures with actionable state.
7. Change normal startup to verify a current supported schema and fail with
   instructions if adoption/upgrade is needed. Fresh-database setup becomes
   an explicit upgrade command; adapt seed and test setup accordingly.
8. Checkpoint: adopt the populated fixture, apply a test-only next migration,
   restart the application, and compare all original rows and API behavior.
9. Add unknown-schema, interrupted-command, repeated-adoption, fresh-install,
   and pending-migration startup tests. A matching repeated adoption should
   report already adopted; it must not insert duplicate history records.
10. Write a runbook: stop all writers, make and verify a consistent backup,
    run status/adoption/upgrade, validate, then restart. Demonstrate restoring
    the backup to a separate test path before treating the workflow as ready.

**Acceptance and verification:** A populated legacy database upgrades with
identical existing IDs, keys, payloads, and UTC timestamps. Unknown structures
remain byte-for-byte untouched where no writes were authorized. Failed upgrades
do not launch workers against a partial schema. Run
`dotnet test --filter "FullyQualifiedName~DatabaseUpgradeTests|FullyQualifiedName~DateTimeOffsetConversionTests"`.

**Rollout and recovery:** Release this foundation before dependent schema
features. Keep the verified pre-upgrade backup; restoring it can discard writes
made after the backup, so never present restore as a lossless live rollback.
Do not delete a provider migration lock blindly after a failed upgrade.

**Common mistake:** Recording a baseline migration as applied merely because
some tables exist, then trusting an unverified schema.

**Done when:** Fresh setup, legacy adoption, failure, and recovery are rehearsed
against data-bearing fixtures and documented as separate paths.

**Teach back:** Explain why "create missing database" and "upgrade this existing
database" are different problems. Which evidence permits baseline adoption?

<a id="sr-02"></a>
## SR-02 — Give project members viewer, editor, or admin access

**User story:** As a project administrator, I want different member roles so
I can let colleagues inspect analytics without granting configuration control.

**Current behavior and learning goal:** Membership currently grants the same
management access to everyone. Add a route-level permission matrix and protect
the last administrator while preserving project non-disclosure to outsiders.

**Open:** [ProjectMembership](../src/Pulse.Domain/Entities/ProjectMembership.cs),
[ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs),
[ProjectEndpoints](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[AuthzMatrixTests](../tests/Pulse.Tests/Api/AuthzMatrixTests.cs), and
[project contracts](../src/Pulse.Api/Contracts/ProjectContracts.cs).

**Proposed contract and invariant:** Add viewer/editor/admin roles. Viewers
may read project data and run read-only analytics; editors additionally manage
dashboards, insights, cohorts, flags, annotations, and exports. Admins additionally
manage members, credentials, retention, erasure, and operational replay. Define
every existing route's minimum role in one reviewable table. New project creators
are admins; newly invited members default to viewer. Add
`PUT /api/projects/P/members/U/role` and `DELETE /api/projects/P/members/U`.
No update/removal may leave a project with zero admins. Known members lacking
permission get 403; non-members retain 404. Project credentials are returned
only to admins, including in existing list/detail responses.

**Implementation steps:**

1. Create `ProjectRoleTests` with admin, editor, viewer, and outsider clients.
   Inventory all endpoint mappings, including top-level capture, decide,
   personal-key management, query routes, and operational routes.
2. Write the explicit permission matrix before coding. Separate query
   execution from configuration mutation; HTTP POST does not inherently mean
   editor access when the route only evaluates a preview.
3. Add Role to ProjectMembership and migrate existing memberships to admin.
   This intentionally preserves existing privileges; document it so operators
   can subsequently reduce roles rather than losing access during upgrade.
4. Extend ProjectAccessService with a minimum-role guard. Keep authentication,
   membership visibility, and role authorization distinct. Preserve project
   read/write key capabilities on their existing SDK/query routes.
5. Apply the matrix to every management handler, including export downloads,
   refreshes, and operational endpoints. Add role-aware safe project DTOs so
   viewer/editor responses cannot expose ApiKey or ReadKey indirectly.
6. Add admin-only member update/removal routes. Within one transaction,
   serialize the project's membership mutation using a project-row write,
   check current roles/admin count, then update/delete. Two simultaneous
   demotions must not each conclude another admin will remain.
7. Validate named roles only; reject numeric enum forms. Return 409 for the
   last-admin invariant, 404 for absent membership, and 400 for invalid role.
8. Checkpoint: a viewer reads a trend but cannot create a flag; an editor
   creates a flag but cannot invite an admin; only admins see project keys.
9. Extend the auth matrix with personal-key callers and same-owner second
   projects. Use barriers to test two-admin demotion/removal races.
10. Update invitation examples, response compatibility notes, and onboarding.
    Audit downstream callers that expected credentials in every project DTO.

**Acceptance and verification:** Every matrix row has representative tests;
role changes affect the next request; project creation and legacy upgrades
retain an admin. Competing demotions leave at least one admin. Secrets are absent
from non-admin serialized responses. Run
`dotnet test --filter "FullyQualifiedName~ProjectRoleTests|FullyQualifiedName~AuthzMatrixTests|FullyQualifiedName~ProjectEndpointsTests"`.

**Rollout and recovery:** Migrate roles before enforcing them, then enable
enforcement with a reviewed route matrix. An application rollback that ignores
roles restores broad access; do not describe that as a harmless rollback.

**Common mistake:** Protecting writes while leaving a read response that hands
viewers the credentials needed to bypass the intended boundary.

**Done when:** Permission enforcement, credential redaction, backfill, and
last-admin behavior are demonstrated together.

**Teach back:** Explain why authentication, membership, role, and credential
type are four related but different checks in this application.

<a id="sr-03"></a>
## SR-03 — Browse a trustworthy management audit trail

**User story:** As an administrator, I want to see who changed a project's
configuration so I can investigate an unexpected flag or permission change.

**Current behavior and learning goal:** Request logging records HTTP metadata,
not durable business changes. Persist audit evidence in the same transaction
as selected mutations so a successful change cannot lack its audit entry.

**Open:** [RequestLoggingMiddleware](../src/Pulse.Api/RequestLoggingMiddleware.cs),
[ProjectEndpoints](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[ProductionReadinessTests](../tests/Pulse.Tests/Api/ProductionReadinessTests.cs).

**Proposed contract and invariant:** Admin-only
`GET /api/projects/P/audit?limit=50&cursor=...&action=flag.updated` returns
metadata entries ordered by monotonic audit sequence descending. Version one
covers member add/role-change/remove and flag create/update/delete. Include
project, actor user ID, optional personal-key ID, action, resource type/ID,
UTC time, and an allowlisted summary. Never store raw credentials, complete
request bodies, person properties, or arbitrary exception strings. Exactly one
audit row accompanies each committed covered mutation; failed mutations produce
no successful-change entry. This is application audit history, not a tamper-proof
ledger against someone with direct database access.

**Implementation steps:**

1. Create `ManagementAuditTests` with one covered member change and flag
   update. List the precise fields expected in each audit summary before coding.
2. Add AuditEntry with generated sequence, ProjectId, actor fields, action,
   resource identity, timestamp, and bounded SummaryJson. Add a project/sequence
   index and SR-01 migration; old changes have no invented retrospective history.
3. Create an explicit audit writer that receives structured fields. It should
   add a row to the caller's DbContext, not save in a separate database or
   independent context. Whitelist summaries by action.
4. Refactor only covered mutations into service operations that own their
   transaction. Include immediate bulk deletes/updates and audit insertion
   together; derive actor identity from authenticated context, never request JSON.
5. Ensure a membership removal records the actor before deleting membership,
   and flag deletion captures resource identity before deleting its row.
   Use stable user IDs rather than retaining mutable display-name snapshots.
6. Add the admin-only list route with a maximum page size of 200, named action
   filter, and a versioned project/filter-bound cursor. Project-scope first,
   order by sequence, and take limit+1 without loading unrelated summaries.
7. Checkpoint: inject an audit-save failure after the mutation has been staged.
   Verify both the business mutation and audit row roll back. Then test a
   successful request creates exactly one correctly attributed entry.
8. Test failed validation, stale updates if SR-04 exists, and failed permission
   checks. None may create a successful-change record. Request diagnostics can
   still record failures separately without masquerading as business history.
9. Add redaction tests using a distinctive fake secret in input fields and
   assert it never appears in audit storage or serialized output.
10. Document supported action coverage, the start of available history, and
    how later scheduled/restoration operations must join the same audit path.

**Acceptance and verification:** Audit and mutation are atomic; JWT and
personal-key actors are distinguishable; foreign-project entries never appear;
cursor ties are resolved by sequence. Run
`dotnet test --filter "FullyQualifiedName~ManagementAuditTests|FullyQualifiedName~ProjectRoleTests|FullyQualifiedName~FeatureFlagTests"`.

**Rollout and recovery:** Add the table before routing covered writes through
the service. Expose coverage/start time in docs. No automatic audit deletion
policy is introduced here; decide retention separately and explicitly.

**Common mistake:** Writing audit entries from middleware after the response,
where persistence failure can no longer roll back the business change.

**Done when:** An injected failure proves atomic evidence, and the supported
coverage and privacy boundaries are reviewable.

**Teach back:** Why is a log saying "PUT returned 200" weaker evidence than an
audit entry committed with the actual flag mutation?

<a id="sr-04"></a>
## SR-04 — Detect competing feature-flag edits

**User story:** As a developer editing a flag, I want stale edits to be rejected
so I do not silently overwrite a teammate's more recent configuration.

**Current behavior and learning goal:** Flag updates currently use last-write
wins. Add a revision precondition enforced by the database, and carry it through
all flag mutations rather than checking only in the HTTP handler.

**Open:** [FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlag](../src/Pulse.Domain/Entities/FeatureFlag.cs),
[feature-flag contracts](../src/Pulse.Api/Contracts/FeatureFlagContracts.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[FeatureFlagTests](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Proposed contract and invariant:** Add Revision starting at 1. Single-flag
reads and successful writes return a strong ETag such as `"flag-ID-r7"` and
revision metadata. Updates and deletes require one matching `If-Match` token:
missing returns 428, malformed/wildcard/multiple tokens return 400, stale
returns 412. A successful configuration update increments revision exactly
once, including a valid no-op update. Creation stays 201 at revision 1.
Missing/foreign flags return 404 after the applicable access guard. `/decide`
values and hashing remain unchanged.

**Implementation steps:**

1. Create `FlagConcurrencyTests` with two clients reading revision 1. Define
   the exact token parser and status precedence before editing the handler.
2. Add Revision and its migration/backfill. Update response mappings and any
   direct seeding paths to initialize valid revisions without changing IDs.
3. Create a central flag mutation service. Accept project/key, expected
   revision, validated changes, and actor context rather than an HTTP header.
4. Read current state for validation, then enforce project, resource ID, and
   expected revision in the actual update/delete statement, or configure an
   EF concurrency token and verify its generated predicate in integration tests.
5. Include revision increment, covered audit entry if SR-03 exists, and all
   configuration fields in the same transaction. A concurrency failure must
   not persist audit/history describing an edit that never happened.
6. Add If-Match parsing at the endpoint boundary. Bind the token to the flag's
   ID so a revision number copied from a different flag cannot authorize a write.
7. Checkpoint: client A updates revision 1 to 2; client B's revision-1 update
   returns 412 and changes nothing. B fetches current config, consciously merges
   its change, and retries using revision 2.
8. Test simultaneous updates using separate contexts and a barrier. At most
   one write based on a given revision succeeds; also test update/delete races.
9. Route later restoration and scheduler writes through the same service with
   their own expected revision. Do not give background code an unconditional
   update shortcut that defeats the user-facing protection.
10. Update client examples and release notes. Explain that a retry must fetch
    and reconcile current state; automatically resending with the latest ETag
    recreates the lost-update problem.

**Acceptance and verification:** Missing, malformed, foreign-resource, stale,
and current tokens have defined outcomes. Rejected writes preserve config,
revision, and audit count; hash golden tests still pass. Run
`dotnet test --filter "FullyQualifiedName~FlagConcurrencyTests|FullyQualifiedName~FeatureFlagTests|FullyQualifiedName~FeatureFlagHasherTests"`.

**Rollout and recovery:** Add revision metadata first and migrate clients to
send If-Match before enabling mandatory preconditions. During any compatibility
window that accepts unconditional writes, explicitly state that protection is
incomplete. Disable the feature only with that consequence understood.

**Common mistake:** Comparing the revision in memory, then saving without
including it in the database mutation's predicate.

**Done when:** A real competing-write test proves the database enforces the
precondition and every flag writer participates.

**Teach back:** Show the two-client lost-update timeline, then point to the
single database predicate that makes the second stale write fail.

## Flag lifecycle and capture admission

<a id="sr-05"></a>
## SR-05 — Inspect and restore a flag's configuration history

**User story:** As a developer, I want to inspect earlier flag configurations
and restore one deliberately so I can recover from an incorrect targeting edit.

**Current behavior and learning goal:** Only the current flag configuration
is stored. Build on SR-04's revision control to preserve immutable configuration
snapshots and make restoration a new change with its own revision.

**Open:** [FeatureFlag](../src/Pulse.Domain/Entities/FeatureFlag.cs),
[FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlagService](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs),
[FlagVariants](../src/Pulse.Domain/FlagVariants.cs), and
[FlagTargetingEdgeTests](../tests/Pulse.Tests/Api/FlagTargetingEdgeTests.cs).

**Proposed contract and invariant:** Add reader-accessible
`GET /api/projects/P/feature-flags/KEY/versions?beforeRevision=...&limit=20`
and editor-only `POST /api/projects/P/feature-flags/KEY/restore` with
`{targetRevision:3}` and SR-04's If-Match header. Return the updated flag and
new ETag. A restore to revision 3 from current revision 8 creates revision 9;
it never changes history or decrements the revision. Retain at most 100 snapshots
per live flag, including current. Missing retained revision returns 404.
Restore copies name, active state, type, rollout, filters, and variants; it
does not change the flag's key, ID, project, or original creation time.

**Implementation steps:**

1. Create `FlagVersionHistoryTests` with revisions for an initial flag and
   two edits. Write the exact snapshot fields and expected restored values.
2. Add FlagVersion keyed uniquely by FlagId/Revision, with ProjectId, bounded
   typed config JSON, recorded time, actor identity, and optional restored-from
   revision. Migrate each existing flag to one baseline snapshot at its current
   revision; mark its origin as baseline rather than inventing an earlier actor.
3. Extend the SR-04 mutation service to write the resulting snapshot in the
   same transaction as every successful flag creation/update. If SR-03 exists,
   include the corresponding audit row in that transaction too.
4. Add descending revision pagination with limit 1–100 and project/resource
   scoping. Expose snapshot configuration only to readers authorized for the
   live flag; do not create a separate route that bypasses project permissions.
5. Implement restoration by loading the retained snapshot and validating it
   against the current supported flag rules. Reject invalid variants or cohort
   references missing from this project with 409 and no mutation.
6. Pass the restored candidate through the same conditional mutation service,
   using the caller's expected current revision. Preserve key-dependent hash
   behavior; restoring fields does not restore historical person/cohort data.
7. Insert the new snapshot, then prune only versions older than the newest 100
   within the transaction. Current version must never be pruned. Deleting a
   flag also removes its version rows, while audit metadata remains if enabled.
8. Checkpoint: restore revision 1 while current is 3 and assert current becomes
   4 with revision-1 configuration. Revisions 1–3 retain identical stored bytes.
9. Test stale restore, pruned target, deleted cohort, save failure, and two
   competing restorations. Rejection must not consume a revision or add history.
10. Document retention and the distinction between restoring configuration
    and recreating an earlier decision for a particular identity.

**Acceptance and verification:** Snapshot counts, pagination, revision growth,
and atomicity match the contract. All writers—including SR-06 if present—create
history. A no-op restoration is still a recorded new revision. Run
`dotnet test --filter "FullyQualifiedName~FlagVersionHistoryTests|FullyQualifiedName~FlagConcurrencyTests|FullyQualifiedName~FlagTargetingEdgeTests"`.

**Rollout and recovery:** Create baseline snapshots before enabling restore.
Pause history pruning while diagnosing a failed rollout; do not rewrite old
snapshots to conceal a bad change. Restoring Active=true is a real behavior
change and must be visible in the request result and audit summary.

**Common mistake:** Updating the flag's revision to the historical number,
which can make old If-Match tokens valid again.

**Done when:** Restoration is a normal concurrency-protected mutation and
historical configuration stays immutable until explicit retention pruning.

**Teach back:** Why can restoring yesterday's targeting produce different
decisions today even when the saved configuration matches exactly?

<a id="sr-06"></a>
## SR-06 — Schedule a future flag rollout change

**User story:** As a developer, I want to schedule one rollout-percentage change
so a planned release can proceed even if my browser is closed at that time.

**Current behavior and learning goal:** Flag updates happen immediately.
Introduce durable scheduling with a stale-revision rule and current permission
check, rather than a timer that keeps a request object in process memory.

**Open:** [FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlag](../src/Pulse.Domain/Entities/FeatureFlag.cs),
[ExportWorker](../src/Pulse.Api/Export/ExportWorker.cs),
[Program](../src/Pulse.Api/Program.cs), and
[FeatureFlagTests](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Proposed contract and invariant:** Editor-only
`POST /api/projects/P/feature-flags/KEY/rollout-schedule` accepts
`{executeAt,rolloutPercentage,expectedRevision}` and returns 202 with schedule
metadata. Allow one pending schedule per flag; time must be 1 minute–30 days
ahead and percentage 0–100. Add GET for its latest schedule and DELETE to cancel
a pending schedule. States are pending/applied/cancelled/conflict/blocked.
At execution, only change RolloutPercentage, and only if the flag revision and
creator's current editor permission still qualify. Otherwise record conflict
or blocked. A restart may delay execution; it must not cause duplicate updates.

**Implementation steps:**

1. Create `ScheduledFlagRolloutTests` with a fake clock. Draw the state machine
   and two competing actions: due execution versus cancellation.
2. Add a schedule entity containing ProjectId, FlagId, creator ID, due time,
   target percentage, expected revision, state, and completion/reason fields.
   Use a unique FlagId row representing the latest schedule; replace terminal
   schedule metadata only when a new schedule is created. No full schedule
   history is promised beyond SR-03 audit entries if installed.
3. Add editor-protected routes with whole-request validation. In a transaction,
   serialize schedule creation per flag, reject an existing pending schedule,
   and store the requested expected revision after checking current state.
4. Add a scoped schedule processor and hosted worker using the existing wake/
   periodic-sweep pattern. Select a bounded batch of due pending schedules.
   Persisted due times, not in-memory timers, are the source of truth.
5. For each candidate, start a short transaction and recheck pending state,
   creator permission, live flag existence, and expected revision. Use SR-04's
   conditional mutation primitive without opening a nested transaction.
6. Commit the flag edit, new revision, optional history/audit, and applied
   schedule state together. Conflicts/blocked creators change only schedule
   state. Return stable reason codes rather than raw exceptions.
7. Implement cancellation as a conditional pending-to-cancelled update. If
   execution already committed, cancellation returns 409; it never reverses
   the applied change. Readers can inspect the recorded outcome.
8. Checkpoint: advance the clock to just before and exactly at the due time.
   Then restart the worker after the due time and prove one application only.
9. Add execution/cancel races, intervening manual edit, creator demotion,
   deleted flag, and transaction-failure tests. A failed transaction remains
   retryable without a partially applied revision.
10. Document that paused workers process overdue schedules after restart and
    that changing a flag manually can intentionally invalidate a pending schedule.

**Acceptance and verification:** Due-time equality executes; a changed revision
produces conflict; a demoted creator produces blocked. Concurrent processors
cannot increment the revision twice for one schedule. Run
`dotnet test --filter "FullyQualifiedName~ScheduledFlagRolloutTests|FullyQualifiedName~FlagConcurrencyTests"`.

**Rollout and recovery:** Install schema/routes with the scheduler disabled,
test due selection using a controlled clock, then enable one scheduler. Stop
new schedule admission and the worker to pause rollout; cancellation and
inspection remain available. Applied changes require a separate new edit.

**Common mistake:** Automatically applying a schedule against the newest
revision after a mismatch, silently overriding an intervening human decision.

**Done when:** Durable timing, permission rechecks, revision conflicts, and
cancel races all follow the documented state machine.

**Teach back:** Explain why "execute at noon" is a due-time promise rather than
a guarantee that the database commits at precisely noon.

<a id="sr-07"></a>
## SR-07 — Retry capture submissions without duplicate admission

**User story:** As an SDK developer, I want to attach a stable client event ID
so retrying after a lost response does not queue the same event repeatedly.

**Current behavior and learning goal:** Worker acknowledgement is atomic with
event writes, but separate client submissions are not deduplicated. Define
idempotency at admission and keep it distinct from worker crash recovery.

**Open:** [CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[capture contracts](../src/Pulse.Api/Contracts/CaptureContracts.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs),
[IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
and [IngestionTransactionTests](../tests/Pulse.Tests/Infrastructure/IngestionTransactionTests.cs).

**Proposed contract and invariant:** Accept an optional UUID `event_id` on each
single/batch event. For seven days after first admission, `(project,event_id)`
with equivalent normalized payload is admitted at most once; a different
payload under that live key returns 409. The whole submitted batch is rejected
on any conflicting key, with zero new admissions from that batch. Requests
without IDs retain current capture behavior. ID-bearing requests return 202
with newly queued and deduplicated counts. The seven-day window never extends
on retries; after expiry, reuse can create a new event. No forever/exactly-once
processing guarantee is claimed.

**Implementation steps:**

1. Create `CaptureIdempotencyTests`: send the same named event twice with one
   client ID, then change a property under that ID and expect conflict.
2. Add nullable IDs to public capture items and normalized envelopes. Add an
   admission-key table with project/client-ID uniqueness, payload hash/version,
   accepted time, expiry, and stable internal admission ID. Do not use the
   client UUID directly as a globally unique AnalyticsEvent primary key.
3. Specify and test a versioned canonical fingerprint: normalized event/name,
   normalized identity, supplied timestamp in UTC or an explicit omitted marker,
   and recursively key-sorted object JSON. Preserve array order and JSON scalar
   types; preserve number spelling in v1 and document that `1` versus `1.0`
   conflicts. Reject duplicate object member names for ID-bearing payloads.
4. Validate the whole batch before writes, including repeated IDs within the
   request. Collapse equivalent repetitions into one admission; conflicting
   repetitions reject the entire batch. Count duplicates explicitly.
5. In one admission transaction, check live keys, replace expired keys only
   after expiry, insert new key rows and queue rows, then commit before signal.
   Use the unique constraint and whole-transaction retry to resolve competing
   admissions; never treat every database exception as a duplicate.
6. Keep worker retries and dead-letter replay attached to the original admission
   identity. Replaying an admitted failure bypasses new client admission checks;
   it must not invent a second admission-key window or block itself as duplicate.
7. Checkpoint: lose the first response intentionally, resubmit, drain the queue,
   and verify one stored event. For a mixed batch with one conflict, verify
   no other new event in that batch was queued.
8. Add a bounded expiry cleanup task, but enforce expiry in admission queries
   too so correctness does not depend on cleanup running on time. Do not delete
   in-flight correlation records needed by SR-08 if installed.
9. Test two hosts admitting the same ID, process restart, project isolation,
   omitted timestamps, exact expiry equality, and canonicalization edge cases.
10. Document SDK guidance: generate the ID once per logical event, retain it
    across retries, and generate a new ID for a genuinely new event.

**Acceptance and verification:** Equivalent retries queue once inside the
window; same ID in another project is independent; conflicts are atomic;
requests without IDs remain compatible. Run
`dotnet test --filter "FullyQualifiedName~CaptureIdempotencyTests|FullyQualifiedName~CaptureEndpointsTests|FullyQualifiedName~IngestionTransactionTests"`.

**Rollout and recovery:** Introduce the schema before accepting IDs. If
deduplication is disabled later, refuse ID-bearing requests with an explicit
unavailable response rather than silently accepting duplicate work under a
previously advertised guarantee. Existing idempotency windows remain meaningful.

**Common mistake:** Hashing server-generated processing timestamps, which makes
an otherwise identical retry look like a new payload.

**Done when:** Lost-response and competing-admission tests demonstrate the
bounded guarantee and all replay paths retain admission lineage.

**Teach back:** Explain client retry deduplication and atomic worker
acknowledgement using two separate crash timelines.

<a id="sr-08"></a>
## SR-08 — Follow a capture receipt through processing

**User story:** As an integrator, I want a durable receipt for a capture request
so I can distinguish accepted work from processed events and inspect failures.

**Current behavior and learning goal:** Capture returns a queued count, while
metrics are aggregate observations. Add durable per-item progress without
claiming that queue disappearance proves successful processing.

**Open:** [CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[QueuedEvent](../src/Pulse.Domain/Entities/QueuedEvent.cs),
[IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs),
and [IngestionOperationsTests](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs).

**Proposed contract and invariant:** An opt-in capture header
`X-Capture-Receipt: true` adds receipt ID and status URL to the 202 response.
Member-only `GET /api/projects/P/capture-receipts/R` returns ordered item states
queued/processed/deadLettered, aggregate counts, and completed event IDs or
dead-letter IDs when authorized. It returns metadata, not raw payloads.
Receipts have no fixed deletion deadline while any item remains queued;
completed receipts expire seven days after their final terminal transition.
A processed state commits with its event and queue acknowledgement; a failure
state commits with its dead letter. Write-key possession alone cannot inspect
project processing history.

**Implementation steps:**

1. Create `CaptureReceiptTests` with a two-item request and one controlled
   processing failure. Draw expected states before acceptance, after commit,
   after one terminal item, and after both terminal items.
2. Add Receipt, ReceiptItem, and a queue-to-item reference. Keep submitted
   ordinals on receipt references and internal admission identity separate
   from queue Seq, which can change on replay. Track a processing item for
   every new admission, even without the receipt header, so a later deduplicated
   request can reference it. Use nullable links for legacy rows.
3. Save receipt metadata and queue references in the same capture admission
   transaction. Cap receipt items at the existing batch limit. Do not expose
   a receipt ID whose corresponding accepted work failed to commit.
4. Extend the capture transaction to set the receipt item's processed state
   and actual new AnalyticsEvent ID before committing acknowledgement. Avoid
   inferring event IDs later from name, identity, or timestamp matches.
5. Extend the dead-letter transaction to set deadLettered and the new letter
   ID together. On replay, keep the same logical item, transition it back to
   queued with an incremented replay generation, and clear current terminal
   pointers while retaining a small transition-history record.
6. If SR-07 exists, link duplicate request receipts to the original admission
   item rather than creating another processing obligation. Preserve input
   order in receipt references; repeated client IDs can reference one item.
   For a legacy deduplication key lacking reconstructable processing metadata,
   reject receipt-enabled admission with 409 before admitting any new batch
   items. Do not fabricate a historical processed state; ordinary duplicate
   admission without a receipt remains supported during the seven-day transition.
7. Add scoped status mapping with counts by receipt reference, documenting
   that duplicate input positions are counted as submitted positions. Derive
   receipt completion from all referenced items, including replay transitions.
8. Checkpoint: crash after event insertion but before commit. After restart,
   there must be neither a durable processed receipt without its event nor
   an event from that uncommitted transaction with a queued receipt.
9. Test mixed outcomes, replay, cross-project access, duplicate references,
   and cleanup. Retain shared admission/item rows until no live receipt or
   deduplication record needs them; purge only eligible terminal history.
10. Document that a receipt describes processing, not perpetual retention:
    a processed event may later be deleted. SR-14/SR-15 must clear retired
    pointers or annotate deletion without making the old capture look pending.

**Acceptance and verification:** State/count transitions match stored outcomes;
replay has visible lineage; cleanup never deletes unfinished work. Legacy
capture responses remain compatible when the header is omitted. Run
`dotnet test --filter "FullyQualifiedName~CaptureReceiptTests|FullyQualifiedName~IngestionTransactionTests|FullyQualifiedName~IngestionOperationsTests"`.

**Rollout and recovery:** Add nullable links first, deploy all processor paths
that maintain them, then enable receipt admission. Old workers unaware of
receipts must not process receipt-bearing rows during rollout.

**Common mistake:** Marking a receipt processed in a separate transaction
after the event commit, leaving a crash window with contradictory evidence.

**Done when:** Receipt states survive crashes and replays, and retention rules
respect shared references rather than deleting needed processing metadata.

**Teach back:** Why is a receipt a durable relationship between records,
instead of just another counter on the metrics endpoint?

## Backlog control and worker recovery

<a id="sr-09"></a>
## SR-09 — Limit a project's pending ingestion backlog

**User story:** As an operator, I want a per-project pending-work limit so one
integration cannot keep growing its queue faster than the worker can drain it.

**Current behavior and learning goal:** Capture has request-rate limiting but
no durable per-project queue-capacity check. Introduce a bound enforced inside
the same transaction as every queue admission, including operational replay.

**Open:** [CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
[QueuedEvent](../src/Pulse.Domain/Entities/QueuedEvent.cs),
[Program](../src/Pulse.Api/Program.cs), and
[IngestionResilienceTests](../tests/Pulse.Tests/Api/IngestionResilienceTests.cs).

**Proposed contract and invariant:** Admin-only
`PUT /api/projects/P/ingestion/limits` sets `{maxPending:null}` for disabled
or an integer 1–100,000. GET shows the configured limit and current pending
count. If a new admission would exceed the limit, capture returns 429 with
`Retry-After: 1` and a stable queue-capacity problem code, queuing none of that
request. A successful admission never takes the project above its configured
limit at commit time. Existing excess work is retained if an admin lowers the
limit; no new work is admitted until there is room. Delayed and leased queue
rows count as pending. Single-letter replay returns the same capacity outcome
and retains its dead letter when capacity is unavailable.

**Implementation steps:**

1. Create `ProjectQueueCapacityTests` with limit 3, two queued rows, and a
   two-item submission. Hold worker processing under test control and expect
   zero new rows from the rejected submission.
2. Add project ingestion settings plus a project admission-gate row/version,
   migrated with limits disabled. Create the gate for new projects as part of
   project creation; use unique ProjectId to prevent duplicate gates.
3. Extract one queue-admission service used by capture and replay. Define how
   SR-07 key checks and SR-08 receipt creation join its caller-owned transaction
   if those features exist; avoid nested transactions.
4. Begin a transaction, acquire a write on the project's gate row, then read
   the effective limit and project-scoped pending count. Compute only genuinely
   new admissions after deduplication, not raw batch length.
5. Reject if current+new exceeds the limit. Otherwise insert all queue rows,
   key records, and receipt records, save, and commit before signalling. Limit
   edits must acquire the same gate so a concurrent reduction cannot be missed.
6. Adapt replay to reserve capacity before consuming its letter. Preserve
   invalid-payload and missing-letter outcomes, and ensure a capacity rejection
   rolls back every planned delete/insert in that replay transaction.
7. Checkpoint: drain one row and retry the earlier two-item request. It now
   fits exactly. A fully deduplicated retry needs zero new slots and can succeed
   even when the current backlog is at or above a newly lowered limit.
8. Test two concurrent admissions with one remaining slot using separate
   hosts/contexts. Only one can consume the slot. Also test a limit edit racing
   with capture and replay racing with capture.
9. Treat database-lock failures separately from capacity rejections; do not
   mislabel a storage failure as a full queue. Whole-operation retries must
   reread the current count and limit with a fresh transaction.
10. Update metrics/runbook examples to explain that Retry-After is guidance,
    not a reservation or a promise that capacity will exist one second later.

**Acceptance and verification:** Exact-capacity admissions succeed, over-limit
batches are atomic, replay retains evidence on rejection, and concurrent
admissions respect the bound. No process-local counter is authoritative. Run
`dotnet test --filter "FullyQualifiedName~ProjectQueueCapacityTests|FullyQualifiedName~CaptureIdempotencyTests|FullyQualifiedName~IngestionOperationsTests"`.

**Rollout and recovery:** Deploy all admission paths before enabling a limit.
Start with an observed project and a documented threshold. Disabling a limit
restores admission but does not make existing backlog disappear; lowering one
never deletes queued data automatically.

**Common mistake:** Counting queue rows before the transaction and allowing
two requests to independently spend the same remaining capacity.

**Done when:** Capture, replay, deduplication, and limit changes share the
capacity invariant under real competing writes.

**Teach back:** Explain why requests per minute and pending queue depth measure
different things and may both need limits.

<a id="sr-10"></a>
## SR-10 — Retry transient ingestion failures with durable backoff

**User story:** As an operator, I want temporary processing failures to retry
at increasing intervals so a busy database is not hammered by immediate retries.

**Current behavior and learning goal:** Ingestion increments Attempts and
reconsiders rows on later sweeps; the row stores no next-attempt time. Add a
durable retry schedule and explicit failure classification while preserving
atomic success/dead-letter behavior.

**Open:** [IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs),
[QueuedEvent](../src/Pulse.Domain/Entities/QueuedEvent.cs),
[IngestionWorker](../src/Pulse.Api/Ingestion/IngestionWorker.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs), and
[IngestionResilienceTests](../tests/Pulse.Tests/Api/IngestionResilienceTests.cs).

**Proposed contract and invariant:** Add NextAttemptAt and a bounded last-error
code to queued work. Initial attempt is immediately eligible. A recognized
transient failure schedules delays of 1, 2, 4, then 8 seconds; the fifth failed
processing attempt moves the row to dead letters. Store failed-attempt count
consistently on both queue and dead letter. Malformed envelopes are permanent
and dead-letter immediately. Cancellation never consumes an attempt. Unknown
infrastructure failures abort/log the cycle without automatically declaring the
payload poison. No wall-clock sleep is held inside a request or transaction.

**Implementation steps:**

1. Create `IngestionRetryScheduleTests` and a pure retry-policy test class.
   Write the attempt-number/delay/state table, including success and cancellation.
2. Add NextAttemptAt with a migration making existing queued rows immediately
   eligible, and add an index supporting due selection. Keep EnqueuedAt as the
   original queue-age timestamp rather than replacing it on each retry.
3. Introduce a failure classifier based on concrete known exception/error
   categories, not substring matching arbitrary exception messages. Start with
   explicitly recognized SQLite contention and envelope validation; treat
   unknown storage faults as operational errors needing investigation.
4. Select only due rows, ordered by existing Seq among eligible rows. Use
   injected TimeProvider and an explicit batch cap. Delayed early rows may be
   overtaken; this queue does not promise strict causal ordering under retries.
5. On a transient failure, roll back processing, clear/dispose the failed
   context, then persist attempt count, next due time, and safe error code as
   one conditional queue update. If that update cannot commit, retain the old
   row and retry the cycle rather than reporting a durable delay that never saved.
6. Keep success/event acknowledgement and permanent/exhausted dead-letter
   transitions atomic. If SR-08 exists, update its item state in those same
   transactions; if SR-11 exists, include its ownership predicates everywhere.
7. Adjust worker drain logic to distinguish no eligible work from an empty
   queue. A bounded periodic sweep is acceptable; it must not busy-loop while
   delayed rows exist or wait forever for a new signal.
8. Checkpoint: fail twice, restart the host before the next due time, and prove
   the stored schedule survives. Advance the clock to equality and process once.
9. Test five failures, permanent invalid input, cancellation, an unclassified
   exception, failed retry-state persistence, and another healthy project's work.
10. Expose delayed-row information in member diagnostics and document the
    attempt definition so operators can interpret old versus new failure records.

**Acceptance and verification:** Due equality is eligible, no attempt runs
early under the controlled clock, cancellation consumes none, and success after
retries still creates one event. Fifth failure records five failed attempts.
Run `dotnet test --filter "FullyQualifiedName~IngestionRetryScheduleTests|FullyQualifiedName~IngestionRetryPolicyTests|FullyQualifiedName~IngestionResilienceTests"`.

**Rollout and recovery:** Upgrade readers/writers before enabling delayed
selection. Older workers ignore NextAttemptAt, so mixed execution would violate
backoff. Disabling scheduling must not delete rows or reset attempt history.

**Common mistake:** Scheduling a delay only in memory, so a restart forgets
the backoff and immediately retries every failing row.

**Done when:** Time, attempt accounting, failure classification, and restart
behavior are verified without introducing sleeps into correctness tests.

**Teach back:** Explain why database contention is evidence about the current
environment, while malformed envelope JSON is evidence about the work item.

<a id="sr-11"></a>
## SR-11 — Run two ingestion workers with project ownership

**User story:** As an operator, I want a second application instance to help
process different projects and recover abandoned work without duplicating events.

**Current behavior and learning goal:** CaptureService explicitly assumes a
single ingestion worker. Add per-project leases and fenced mutations; assigning
a row to a worker is not enough if an expired worker can still commit changes.

**Open:** [IngestionWorker](../src/Pulse.Api/Ingestion/IngestionWorker.cs),
[IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs),
[IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs), and
[IngestionTransactionTests](../tests/Pulse.Tests/Infrastructure/IngestionTransactionTests.cs).

**Proposed contract and invariant:** Version one supports two instances using
the same supported local SQLite database environment. Only the current lease
owner may commit ingestion, retry, or dead-letter mutations for a project.
Each project lease has owner token, increasing generation, and expiry. Lease
duration is 30 seconds; renew between bounded row operations. Different
projects may be owned by different instances; one project's identity changes
remain serialized. This adds fault tolerance and project-level scheduling,
not a promise of linear throughput or a supported network-filesystem deployment.

**Implementation steps:**

1. Create `IngestionLeaseTests` with two processor instances, separate contexts,
   one file-backed database, a controlled clock, and explicit commit barriers.
   Reproduce the current single-worker assumption in a focused test first.
2. Add ProjectIngestionLease and an index/unique key on ProjectId. Migrate
   existing projects to unowned leases. Use a new random owner token per worker
   process and a generation that increments on every new claim/reclaim.
3. Implement bounded project selection and conditional claiming of unowned or
   expired leases. Recheck eligibility in the claim statement; two contenders
   must not both receive ownership based on the same earlier read.
4. Pass project ID, owner token, and generation into every processing operation.
   In the event transaction, acquire/check the lease row before any identity,
   registry, event, or acknowledgement write. A lost owner rolls back.
5. Hold the database write protection through commit. A lease expiring during
   that already-protected short transaction does not permit a competing owner
   to sneak in; the new claimant must wait and recheck after the transaction.
   Do not hold such transactions across external I/O or arbitrary delays.
6. Fence retry updates and dead-letter moves too. Verify exactly one queue
   row was consumed before committing a failure record, and increment process
   counters only after successful durable outcomes.
7. Renew between rows using the same ownership predicate. On lost lease,
   stop the project's batch immediately and discard tracked entities. Fairly
   cap rows/time per project so one active project does not monopolize selection.
8. Checkpoint: pause worker A outside a transaction, expire/reassign its lease
   to B, then resume A. A must fail its ownership check without changing data;
   B processes the work once.
9. Test crashes after claim and before commit, two projects, identity merges,
   delayed retries if SR-10 exists, and replay admission while processing.
   Measure SQLite lock contention rather than assuming two workers are faster.
10. Document supported storage/process topology, clock-skew assumptions, and
    diagnosis of expired leases. Never offer manual lease deletion as a way
    to let a still-running unfenced old worker keep committing.

**Acceptance and verification:** Competing/stale owners cannot duplicate event
or dead-letter rows; crashed owners' projects become reclaimable; identity
relationships remain valid. Run
`dotnet test --filter "FullyQualifiedName~IngestionLeaseTests|FullyQualifiedName~IngestionTransactionTests|FullyQualifiedName~IdentityChainTests"`.

**Rollout and recovery:** Stop every old worker, migrate, deploy only fenced
workers, then enable the second instance. A mixed old/new worker fleet defeats
the invariant. Returning to one worker still requires ownership checks while
any other instance might remain alive.

**Common mistake:** Checking a lease once before a batch, then letting an
expired owner save later rows after another owner has taken over.

**Done when:** A deliberately resumed stale worker is harmless, and the
multi-instance operating limits are measured and documented.

**Teach back:** Explain the difference between a lease expiry time and a
fencing token using the paused-A/reassigned-B timeline.

<a id="sr-12"></a>
## SR-12 — Cancel and recover asynchronous exports

**User story:** As an analyst, I want to cancel unnecessary exports and have
abandoned work recover after a worker restart so jobs do not stay running forever.

**Current behavior and learning goal:** ExportJobProcessor marks Pending jobs
Running and selects only Pending jobs on later cycles. Add durable cancellation
and ownership-aware recovery without publishing output from a cancelled attempt.

**Open:** [ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs),
[ExportWorker](../src/Pulse.Api/Export/ExportWorker.cs),
[ExportEndpoints](../src/Pulse.Api/Endpoints/ExportEndpoints.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract and invariant:** Editor-only
`POST /api/projects/P/exports/J/cancel` returns 202 for a Running job after
recording CancelRequested, and 200 for a Pending job made Cancelled immediately.
Repeated cancellation of CancelRequested/Cancelled is successful and returns
current state; Completed/Failed returns 409. Add attempt generation, owner token,
lease expiry, and last heartbeat. Expired Running work is reclaimed and restarted
from the beginning; expired CancelRequested work becomes Cancelled. Only the
current running owner may publish a completed result. Cancellation must never
be overwritten by a late successful worker save.

**Implementation steps:**

1. Create `ExportCancellationRecoveryTests` and draw every allowed state
   transition. Include cancel versus final completion as a contested transition.
2. Add states/lease fields and a migration. Existing Running rows become
   recoverable unowned work under an explicit deployment-time backfill; preserve
   Completed/Failed documents and their timestamps.
3. Replace load-all-Pending processing with a bounded conditional claim per
   job. Assign a fresh owner/attempt, lease duration, and heartbeat before
   expensive export computation begins.
4. Add scoped cancellation routes with conditional status transitions. Clear
   unpublished output for cancelled jobs; keep cancellation reason/state
   separate from an exception-based Failed result.
5. Poll current ownership/cancellation between export pages and before final
   publication. Run queries outside long write transactions; heartbeat through
   short conditional updates. Thread cancellation into the active query too.
6. Publish ResultContent, ContentType, RowCount, CompletedAt, and Completed
   status using a predicate requiring the same owner/attempt and Running state.
   If it matches zero rows, discard the computed output and reload status.
7. Add a bounded recovery sweep for expired leases. Increment/fence attempts
   before a new owner runs; an old attempt's late result can no longer match.
   A CancelRequested expired job is finalized as Cancelled, not restarted.
8. Checkpoint: stop a worker after it claims a job, restart after expiry, and
   download the recovered result. Then resume the old worker and prove its
   final publication is rejected.
9. Test cancellation before claim, mid-page, immediately before completion,
   after completion, and during shutdown. Do not convert host cancellation
   into Failed; let lease recovery handle interrupted active work.
10. Keep the existing row caps/rendering semantics documented. This story adds
    recovery and cancellation, not streaming storage or unlimited-size exports.
    Update history/status consumers for the additional named states.

**Acceptance and verification:** No stuck Running job after controlled lease
expiry; cancelled jobs never become downloadable; late attempts cannot publish;
completion/cancel races produce one valid terminal outcome. Run
`dotnet test --filter "FullyQualifiedName~ExportCancellationRecoveryTests|FullyQualifiedName~ExportTests"`.

**Rollout and recovery:** Deploy schema and new state readers before enabling
cancel/recovery. Stop old export workers, which do not understand ownership or
CancelRequested. Disable new claims to pause processing; do not erase lease
fields or reset every job blindly to Pending.

**Common mistake:** Cancelling only an in-memory token, which is lost when the
process restarts and is invisible to a different worker instance.

**Done when:** Cancellation, final publication, and abandoned-job recovery
share an enforced state machine with stale-owner tests.

**Teach back:** Why does checking CancelRequested before calculation still
require another condition in the final publish statement?

## Data lifecycle and token boundaries

<a id="sr-13"></a>
## SR-13 — Export a fixed snapshot of event data

**User story:** As an analyst, I want an event export whose contents stay fixed
across worker retries so a download does not mix different views of changing data.

**Current behavior and learning goal:** ExportJobProcessor pages through live
event rows. A cursor is not a database snapshot, especially when identity merges,
deletions, or late events can change the underlying dataset. Persist a bounded
copy of export inputs before rendering them.

**Open:** [ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs),
[ExportService](../src/Pulse.Infrastructure/Services/ExportService.cs),
[ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract and invariant:** Extend asynchronous event-export creation
with `consistency:"snapshot"`; keep existing live mode compatible. Snapshot v1
supports event-name and date-range selection only, JSON/CSV output, at most
10,000 rows and 16 MiB of serialized snapshot data. Unsupported property filters
return 400; exceeding a snapshot cap fails the job with a named limit reason,
never a silently truncated document. Return snapshotCapturedAt after capture.
The snapshot represents the database view taken by the worker, not submission
time. Once captured, retries render those same copied event values, including
then-current PersonId and properties. Requires SR-12 ownership/recovery.

**Implementation steps:**

1. Create `EventExportSnapshotTests`: create a job, capture its snapshot,
   then insert a late event, merge an identity, and delete a source event.
   The eventual snapshot download should retain its original copied values.
2. Add snapshot metadata to ExportJob and an ExportSnapshotRow table keyed
   by job/ordinal. Store only the full event-export row fields needed to render
   output, not references that will later reread mutable event/person rows.
3. Validate snapshot-specific inputs during job admission. Preserve the
   existing inclusive export date semantics and require a bounded explicit
   range of at most 90 days for snapshot mode.
4. After claiming the job, start a transaction that verifies current ownership,
   takes the scoped event read snapshot, and copies rows in timestamp/ID order.
   Scan limit+1 and track serialized byte size so either cap causes rollback.
5. Persist snapshot rows and snapshotCapturedAt atomically, along with an
   explicit snapshot-ready marker. Cancellation/ownership checks happen inside
   this short bounded stage; no ready marker may exist for an incomplete copy.
6. Render from immutable snapshot rows outside the capture transaction, in
   ordinal pages, using the existing CSV/JSON rendering semantics. SR-12's
   cancellation and fenced publication still apply.
7. On worker recovery, if the ready marker exists, reuse the stored input;
   otherwise capture a new snapshot and report its later capture time. Do not
   claim uncommitted partial input was the authoritative first snapshot.
8. Checkpoint: terminate rendering halfway through, restart with ownership
   recovery, and verify the final bytes match a no-crash rendering of the same
   saved snapshot. This is reproducible input, not necessarily streaming output.
9. Test cap failures, empty snapshots, cancellation during capture/render,
   concurrent source changes, and foreign-project isolation. Check SQL rows
   directly to prove no partial snapshot-ready state can commit.
10. Delete snapshot rows when their job is deleted/cancelled or explicitly
    expired, using coordinated transactions. Integrate SR-15 erasure so copied
    data cannot evade project export invalidation.

**Acceptance and verification:** After readiness, source changes do not alter
the export; before readiness, rollback leaves no authoritative partial copy.
Over-cap jobs fail explicitly, and stale owners cannot publish. Run
`dotnet test --filter "FullyQualifiedName~EventExportSnapshotTests|FullyQualifiedName~ExportCancellationRecoveryTests|FullyQualifiedName~ExportTests"`.

**Rollout and recovery:** Enable snapshot mode only after migration and worker
support are complete. Disable new snapshot admission to pause the feature while
continuing to serve existing completed documents. Measure capture-transaction
duration and SQLite contention at the chosen caps before enabling it broadly.

**Common mistake:** Recording only a last event ID and calling it a snapshot,
while subsequent pages still read mutable source rows.

**Done when:** Fixed input, restart reuse, cap behavior, and copied-data cleanup
are verified with actual mutation between capture and rendering.

**Teach back:** Explain the difference between a cursor position, a timestamp
cutoff, and copied snapshot rows. Which survives changes to existing events?

<a id="sr-14"></a>
## SR-14 — Retain events for a configured number of days

**User story:** As an administrator, I want older processed events removed on
a schedule so I can control the project's stored analytics history.

**Current behavior and learning goal:** Events have no automatic retention
policy. Implement an explicitly enabled, bounded cleanup workflow with a
preview and durable progress, without claiming to erase every copy of data.

**Open:** [AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs),
[DataManagementEndpoints](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[DataManagementTests](../tests/Pulse.Tests/Api/DataManagementTests.cs).

**Proposed contract and invariant:** Admin-only retention GET/PUT under
`/api/projects/P/retention` stores `{enabled,days}` with days 30–3,650 and a
policy revision. All migrated/new projects start disabled. A preview route
returns the policy revision, fixed UTC cutoff, and current eligible row count.
Cleanup removes only Events with `Timestamp < cutoff` in batches of at most
1,000. Store each run's cutoff, policy revision, progress, and removed count.
Do not delete people, registry definitions, cohorts, queued work, or exports.
Late events older than the horizon may exist until a later sweep; retention
here is eventual processed-event cleanup, not admission rejection or total erasure.

**Implementation steps:**

1. Create `EventRetentionTests` with events just before, at, and after an
   explicit cutoff, including null-PersonId events and another project's data.
2. Add ProjectRetentionPolicy and RetentionRun with an index supporting
   project/timestamp deletion. Migrate disabled policies and test legacy rows
   remain unchanged before any policy is enabled.
3. Add admin-only policy/preview routes. Use conditional policy revisions for
   updates so an old settings page cannot silently overwrite a newer policy.
   Describe preview as an observation, not a promise of a later exact count.
4. Add a hosted cleanup worker and scoped processor. Create at most one active
   run per project and capture its cutoff once from the chosen policy/time.
   Process a bounded number of batches per cycle to avoid monopolizing SQLite.
5. Before each batch, verify the policy is still enabled at the recorded
   revision. Stop/supersede the run on change; never apply an old shorter
   retention period after an admin has lengthened or disabled the policy.
6. Select at most 1,000 eligible IDs, delete them with project and cutoff
   predicates, and update run counters/progress in one transaction. Serialize
   policy updates with this batch decision so a stale check cannot race a change.
7. If SR-08 exists, clear deleted event pointers or record a retained processed
   outcome with dataRemoved=true in the same lifecycle path. Processing history
   must not claim that deletion made an old item queued again.
8. Checkpoint: interrupt after a committed batch, restart, and resume using the
   run's fixed cutoff. A newly inserted older event is picked up by this or a
   subsequent run; document that exact live counts may change during cleanup.
9. Test disable/lengthen races, multiple batches, transaction failure, repeat
   sweeps, boundary equality, and project isolation. Verify only eligible event
   rows change and large JSON payloads are not materialized for deletion.
10. Add a preview/enable/observe/disable runbook with an explicit statement
    that a policy change cannot restore already deleted rows.

**Acceptance and verification:** At-cutoff events survive; disabled policies
delete nothing; changed policy revisions prevent later stale batches; interrupted
runs resume without double-counting committed deletions. Run
`dotnet test --filter "FullyQualifiedName~EventRetentionTests|FullyQualifiedName~DataManagementTests|FullyQualifiedName~QueryEdgeCaseTests"`.

**Rollout and recovery:** Ship disabled, validate previews on representative
fixtures, then enable one project with a reviewed retention period. Stop new
batches to pause cleanup. Restoring deleted events requires a separate recovery
source and can affect data written since that source was captured.

**Common mistake:** Describing event-table retention as deletion of old export
documents, person properties, or backup copies that the worker never touches.

**Done when:** Eligibility, durable progress, policy-change behavior, and
irreversibility are explicit and demonstrated.

**Teach back:** Explain why "30-day retention" needs a chosen timestamp field,
boundary rule, sweep schedule, and definition of which data copies it covers.

<a id="sr-15"></a>
## SR-15 — Erase a person and block known identities from returning

**User story:** As an administrator, I want to erase a person's project data
and suppress their known identities so delayed SDK events cannot immediately
recreate the records I just removed.

**Current behavior and learning goal:** Current person deletion removes several
related tables, but queued/replayed events can resolve identities again. Treat
erasure as a durable workflow across admission, processing, and copied exports.
This is a defined product capability, not a claim of legal compliance.

**Open:** [DataManagementEndpoints](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs),
[IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
and [IdentityTests](../tests/Pulse.Tests/Api/IdentityTests.cs).

**Proposed contract and invariant:** Admin-only
`POST /api/projects/P/persons/U/erasure` creates a durable job and returns 202
with a status URL. Freeze the person's currently known distinct IDs, install
project-scoped suppression fingerprints, and temporarily pause project data
operations while erasing. States are pending/running/needsReview/completed/failed.
Suppress future capture/replay when the normalized distinct ID or identify
anonymous-ID field matches a blocked identity; return 422 without storing that
payload. Unknown future aliases cannot be inferred as the same real person.
Conservatively invalidate all project exports during erasure because current
opaque export documents lack reliable per-person lineage; disclose this effect
in the contract. Suppression has no automatic expiry in v1.

**Implementation steps:**

1. Create `PersonErasureWorkflowTests` with aliases, events, static cohort
   membership, queued/dead-letter envelopes, and completed/pending exports.
   Draw the full inventory and explicitly mark project-wide export invalidation.
2. Add ErasureJob, project maintenance state/generation, and IdentitySuppression.
   Store keyed fingerprints of normalized project/identity values using a
   dedicated deployment secret and recorded key version. Do not store the raw
   aliases in long-lived suppression rows; never log them during cleanup.
3. Add a common project data gate used by capture, replay, ingestion commits,
   export claims/publication, and relevant data reads. Pause acquisition must
   serialize against those operations; in-flight transactions either finish
   before the pause or fail a generation check before committing afterward.
4. In one initiation transaction, verify U belongs to P, freeze its aliases,
   install fingerprints, mark the project paused, and create the job. Permit
   erasure-status/admin recovery routes while other project data operations
   return a documented 503 maintenance response.
5. Process bounded batches deleting the person's events/mappings/cohort links,
   matching queued/dead-letter envelopes, and eventually the Person row.
   Match both identity fields used by `$identify`; do not search JSON substrings.
6. Fence/cancel every project export attempt, remove downloadable content and
   SR-13 snapshot copies if present, and prevent a stale worker from republishing.
   Clear or redact linked receipt/history pointers while preserving truthful
   terminal outcomes; add a suppressed outcome when SR-08 is installed.
7. For malformed envelopes that cannot be classified safely, set needsReview
   and keep the project paused. Provide a metadata-only report and admin-only
   `POST /api/projects/P/erasure-jobs/J/discard-unreadable`, accepting at most
   100 explicitly selected queue/letter IDs from that report. Recheck project,
   job, and recorded content fingerprints before deleting those exact rows,
   recording the decision, and resuming the job. Declining leaves it paused;
   never silently discard unreadable unrelated payloads or declare completion.
8. Before unpausing, verify the inventory contains no targeted active records
   and all export publications are fenced. Mark completed and release the pause
   together. Keep suppression checks active on every later admission/processing
   path, including events accepted before a pause but retried afterward.
9. Replace or explicitly deprecate the old immediate person-delete route so
   it cannot bypass this contract. Document the changed asynchronous response
   and require existing clients to follow job status.
10. Test restart at each phase, an SDK retry, an identify alias, replay, a stale
    export worker, secret-version availability, and malformed-envelope review.
    Refuse startup/processing when a required suppression key version is missing.

**Acceptance and verification:** Known identities cannot recreate data after
completion; unrelated people's records remain; project exports are invalidated
as disclosed; no stale worker resurrects output. Unknown aliases remain an
explicit limitation. Run
`dotnet test --filter "FullyQualifiedName~PersonErasureWorkflowTests|FullyQualifiedName~IdentityTests|FullyQualifiedName~IngestionOperationsTests"`.

**Rollout and recovery:** This is the broadest story; split gate enforcement,
suppression, and cleanup into separate reviewed changes. Enable only after all
writers participate. Failure keeps the maintenance state inspectable; disabling
the feature must not remove suppression or blindly resume incomplete erasure.
Backups and third-party copies need separately defined handling.

**Common mistake:** Deleting the visible Person row and assuming no queued,
copied, or newly submitted data can recreate it.

**Done when:** Every inventoried data path and restart phase is tested, and
the known-identity, maintenance, export, and backup limitations are explicit.

**Teach back:** Explain why deletion, suppression, and copied-data invalidation
are three different responsibilities in the same user request.

<a id="sr-16"></a>
## SR-16 — Issue project-scoped, expiring personal tokens

**User story:** As a developer automating one project, I want a token limited
to selected capabilities and an expiry so a script does not inherit all my access.

**Current behavior and learning goal:** Personal keys currently authenticate
as a user with all current memberships. Add token-specific restrictions that
intersect with current membership roles rather than replacing those checks.

**Open:** [PersonalApiKeyAuthenticationHandler](../src/Pulse.Api/Auth/PersonalApiKeyAuthenticationHandler.cs),
[PersonalApiKey](../src/Pulse.Domain/Entities/PersonalApiKey.cs),
[AuthEndpoints](../src/Pulse.Api/Endpoints/AuthEndpoints.cs),
[ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs), and
[AuthzMatrixTests](../tests/Pulse.Tests/Api/AuthzMatrixTests.cs).

**Proposed contract and invariant:** JWT-authenticated users can create a
restricted key with name, 1–20 project IDs, selected named scopes, and expiry
1 minute–90 days ahead. Version-one scopes are analytics:read,
configuration:read, configuration:write, and exports:write. Extend SR-02's route
matrix with required scopes. Restricted keys cannot manage tokens, members,
credentials, retention, erasure, or audit administration regardless of scopes.
Effective access is current user role AND token project allowance AND required
scope AND unexpired/unrevoked key. Plaintext is shown once; only its hash and
display suffix remain. Existing keys are explicitly marked legacy unrestricted.

**Implementation steps:**

1. Create `ScopedPersonalTokenTests` with a user in projects A/B, a token
   restricted to A, and a subset of capabilities. Write the expanded route
   matrix, including read-only POST previews and export creation/download.
2. Add expiry, restriction mode, and token-project/scope mappings. Migrate
   existing keys to legacy mode without silently shortening their lifetime.
   Keep the existing key-hash uniqueness and never persist plaintext.
3. Add a dedicated restricted-token creation contract/route, requiring an
   actual JWT session. Require JWT for other token-management creation paths
   too, so a restricted key cannot mint an unrestricted replacement through
   the old endpoint. Validate membership in every requested project.
4. Update authentication to reject expiry at equality and revoked/deleted
   keys, and attach key identity/restriction metadata to the authenticated
   principal. Do not cache permissions for the token's whole lifetime.
5. Enforce restrictions centrally with the role/scope matrix and scoped
   resource guards. Deny unclassified protected routes for restricted keys
   until deliberately assigned a capability; avoid permission by HTTP verb alone.
6. Filter project lists to allowed projects. Omit project credentials for
   every restricted-token response, even if its user is an admin; role-only
   redaction would let the token recover broader project keys. Allow only the
   explicitly documented own-account `/me` global read; reject other global
   management operations unless intentionally mapped.
7. Recheck current membership/role on use. A removed membership or demoted
   user must lose the corresponding token capability on the next request even
   though the token string and stored scopes are unchanged.
8. Checkpoint: configuration:read can list flags but not update them; adding
   configuration:write still cannot update project B or manage project A members.
9. Test exact expiry, revocation, role demotion, a foreign project, token
   management bypasses, legacy mode, and scoped personal-key audit attribution
   if SR-03 exists. Use a controlled clock rather than waiting for expiry.
10. Document scope-to-route mappings and one-time key display. Return no-store
    on creation responses and ensure request/audit logs never include token values.

**Acceptance and verification:** No scope grants more than the user's current
role; disallowed projects remain invisible; restricted tokens cannot mint keys;
expiry/revocation take effect on subsequent requests. Run
`dotnet test --filter "FullyQualifiedName~ScopedPersonalTokenTests|FullyQualifiedName~AuthEndpointsTests|FullyQualifiedName~AuthzMatrixTests"`.

**Rollout and recovery:** Deploy scope-aware authentication and guards before
issuing restricted tokens. Older application instances ignore these restrictions
and must not serve them. Revoking a token is supported; converting it silently
to unrestricted mode is not a recovery strategy.

**Common mistake:** Storing scope claims on the token but leaving existing
handlers to authorize only by UserId, which bypasses the intended restriction.

**Done when:** All protected routes have a deliberate role/scope decision and
negative tests exercise alternate endpoints, not just the new creation route.

**Teach back:** If a token says configuration:write but its user is now a viewer,
which permission wins, and where is that intersection enforced?

## Diagnostics, analytics, and product automation

<a id="sr-17"></a>
## SR-17 — Trace one capture through the background worker

**User story:** As a developer debugging ingestion, I want to follow a capture
request into queue processing and replay so I can locate where work failed.

**Current behavior and learning goal:** RequestLoggingMiddleware logs HTTP
method/path/status/duration, while workers run outside that request context.
Carry diagnostic context across the durable queue without confusing tracing
with authentication, processing receipts, or business audit history.

**Open:** [RequestLoggingMiddleware](../src/Pulse.Api/RequestLoggingMiddleware.cs),
[CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[QueuedEvent](../src/Pulse.Domain/Entities/QueuedEvent.cs),
[IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs),
and [IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs).

**Proposed contract and invariant:** Add an `X-Trace-Id` response header for
capture and include the same trace ID in its admission and processing log
events. Store validated trace-parent context separately from the payload.
Each processing attempt gets its own span; replay starts a span in the replay
request's trace with a link to the original context. Use the platform Activity
API and a named ActivitySource; external telemetry export is optional and
disabled by default. No payloads, raw distinct IDs, credentials, or arbitrary
baggage values become persisted trace metadata. Diagnostic loss must not lose
or duplicate business work.

**Implementation steps:**

1. Create `IngestionTraceTests` with an in-process activity listener and
   captured structured logs. Draw separate spans for capture, queue commit,
   processing attempt, and a later replay request.
2. Add nullable bounded trace-parent columns to QueuedEvent and DeadLetterEvent,
   plus an optional original-context link for replayed work. Keep legacy rows
   valid with null context. Add a small shared tracing helper using platform
   parsing rather than splitting a raw header by hand.
3. At admission, use the validated active server activity context; if no
   usable context exists, create one through the tracing helper. Return only
   the safe trace ID and store its parent context alongside queue metadata.
4. Preserve batch semantics: items from one request share the producer trace
   context but acquire distinct processing-attempt spans. If SR-07 deduplicates
   an item, record a duplicate-admission diagnostic rather than inventing a
   second processing span for work that was not queued.
5. Start and dispose a consumer activity for each attempt, using the stored
   parent when valid. Add bounded tags such as outcome, attempt number, and
   queue sequence; never use raw person properties as tags or metric labels.
6. Carry original context into dead letters during their atomic move. Replay
   stores new producer context and a bounded original-context link rather
   than extending one endless parent chain through every operational retry.
7. Update request/worker structured logs with trace/span IDs. Keep existing
   request status/timing behavior intact, and do not make business persistence
   depend on an external tracing collector being reachable.
8. Checkpoint: capture, force a processing failure, replay, and inspect the
   relationships. The replay request is identifiable separately while retaining
   a link back to the original failed work.
9. Test no listener, disabled sampling, invalid/oversized stored context,
   process restart, batch requests, and payloads containing distinctive fake
   secrets. No trace context error should become a poison event by itself.
10. Document how to search local logs by X-Trace-Id and how optional telemetry
    export can be configured later. Correlation IDs supplied by clients are
    untrusted labels and never grant access to project data.

**Acceptance and verification:** Context survives the queue/restart boundary;
attempt spans are distinct; replay links correctly; tracing disabled preserves
capture/processing results; secret markers are absent from trace metadata.
Run `dotnet test --filter "FullyQualifiedName~IngestionTraceTests|FullyQualifiedName~IngestionPipelineTests|FullyQualifiedName~ProductionReadinessTests"`.

**Rollout and recovery:** Add nullable columns before writers use them, then
enable instrumentation locally. An absent listener/collector must leave the
application functional. Stopping export of telemetry does not require removing
correlation metadata needed for in-flight work.

**Common mistake:** Treating one span ID as the identity of an event across
all retries, or placing user-controlled high-volume values into metric labels.

**Done when:** A developer can trace an actual failed/replayed fixture across
process boundaries without receiving raw customer payloads in diagnostics.

**Teach back:** Explain how a trace, a processing receipt, and an audit entry
answer three different questions about the same workflow.

<a id="sr-18"></a>
## SR-18 — Run trends with explicit work budgets

**User story:** As an analyst using a busy project, I want a trend request to
either return a complete result within defined work limits or explain how to
narrow it, instead of consuming unbounded application memory.

**Current behavior and learning goal:** QueryService loads matching events and
related data into memory before aggregation. Add an opt-in bounded trend path
with streaming aggregation and explicit rejection of incomplete results.
No schema migration or prior story is required for this feature.

**Open:** [QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[TimeBucket](../src/Pulse.Domain/TimeBucket.cs),
[PropertyFilters](../src/Pulse.Domain/PropertyFilters.cs), and
[QueryEdgeCaseTests](../tests/Pulse.Tests/Api/QueryEdgeCaseTests.cs).

**Proposed contract and invariant:** Add read-access
`GET /api/projects/P/insights/trend-bounded` with event, explicit from/to,
hour/day/week interval, and optional event-targeted filters only. Use existing
inclusive trend boundaries and response bucket semantics. Reject person/cohort
filters and breakdown with 400 in v1. Budgets are 10,000 scanned events, 8 MiB
of scanned property JSON, 2,161 buckets, 1,000 annotations, and a two-second
server execution deadline. Row/byte/bucket/annotation excess returns 422 with
a reason and narrower-query guidance; server deadline returns 504. Never return
an incomplete trend as a successful complete result. The deadline requests
cooperative cancellation; provider cleanup can make the response take longer
than two seconds. Client cancellation remains client cancellation. Existing
trend endpoints remain unchanged.

**Implementation steps:**

1. Create `BoundedTrendTests` and pure budget-policy tests. Write a small
   fixture whose expected buckets exactly match the existing trend route,
   including tied timestamps, zero-filled buckets, and null PersonId behavior.
2. Add a reusable work-budget object tracking rows, bytes, buckets, and deadline
   cancellation. Keep policy decisions separate from HTTP mapping so SR-19
   can reuse the mechanism with different fixed limits.
3. Validate inputs and calculate bucket count before querying. Require a
   nonempty range of at most 90 days and named intervals. Handle timestamp
   arithmetic limits explicitly instead of overflowing while enumerating buckets.
4. Add a scoped bounded-query service. Build project/event/time predicates
   in SQL, select only needed fields, and stream rows in a deterministic order
   with cancellation. Read at most the event cap plus one to detect overflow.
5. Count scanned rows/bytes before applying in-memory event filters, so a
   highly selective filter cannot hide an expensive scan. Skip property JSON
   projection when no filter needs it. One oversized row can still be allocated
   by the provider before its byte size is checked; document that practical limit.
6. Aggregate into bounded bucket counters and per-bucket distinct PersonId
   sets. Preserve the existing engine's null-ID counting semantics deliberately;
   changing that metric is a separate compatibility decision, not an optimization.
7. Fetch annotations with their own limit+1 guard. Buffer only the bounded
   aggregate response until all checks pass; do not start streaming a 200 body
   and discover budget failure halfway through it.
8. Checkpoint: compare bounded and existing routes on the same small fixtures.
   Then exceed each budget individually and verify no partial chart is returned.
9. Test deadline cancellation with a controlled slow query seam and explicit
   signalling, separately from client-request cancellation. Verify readers and
   contexts dispose correctly after either path.
10. Measure allocations and query duration at several input sizes. Record the
    observed benefit and provider limits; this endpoint does not bound the
    older query paths or all database work across the application.

**Acceptance and verification:** Within-budget results match current semantics;
every configured cap has an exact boundary test; scans count nonmatching rows;
deadline and client cancellation remain distinct. Run
`dotnet test --filter "FullyQualifiedName~BoundedTrendTests|FullyQualifiedName~QueryBudgetPolicyTests|FullyQualifiedName~QueryEdgeCaseTests"`.

**Rollout and recovery:** Add this as an opt-in route, compare results with
existing queries, then move selected callers deliberately. Keep budget failures
observable through safe reason codes. Increasing limits requires new capacity
measurements rather than merely hiding frequent 422 responses.

**Common mistake:** Applying Take(10000), aggregating those rows, and returning
200 without checking whether more matching input exists.

**Done when:** Complete-result semantics, bounded aggregation, cancellation,
and measured resource behavior are demonstrated together.

**Teach back:** Explain why limiting output buckets does not limit scanned
events, distinct-person state, or expensive filter evaluation.

<a id="sr-19"></a>
## SR-19 — Explore activity sessions for a person

**User story:** As a support engineer, I want a person's events grouped into
activity sessions so I can distinguish separate visits within a selected period.

**Current behavior and learning goal:** Pulse stores individual events and
identity mappings, not sessions. Derive sessions using a precise inactivity
rule while making window truncation and current identity merges visible.
Reuse SR-18's budget mechanism, not its event-filter contract.

**Open:** [PersonEndpoints](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs),
[IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs),
[QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs), and
[IdentityChainTests](../tests/Pulse.Tests/Api/IdentityChainTests.cs).

**Proposed contract and invariant:** Member/read-role
`GET /api/projects/P/persons/U/sessions?from=...&to=...&gapMinutes=30`
returns `{personId,from,to,gapMinutes,boundarySemantics:"window-local",sessions}`.
Require a nonempty half-open range `[from,to)` of at most seven days and gap
1–120 minutes. Group this person's processed events, ordered Timestamp then ID;
a gap greater than or equal to the threshold starts a new session. Each result
contains ordinal, first/last timestamps, first/last event IDs, event count, and
observedDurationSeconds. Flag the first session mayStartBeforeWindow and the
last mayContinueAfterWindow. No lookbehind/lookahead is performed, so these
flags are deliberately conservative. Cap input at 10,000 events and output
at 500 sessions; excess is 422, not a partial session list.

**Implementation steps:**

1. Create `PersonSessionTests` and pure `SessionGroupingTests`. Draw events
   at 10:00, 10:10, and 10:40 with a 30-minute gap: expect two sessions with
   counts 2/1 and observed durations 600/0 seconds.
2. Define a small Domain event-position record and pure session grouper.
   It must not query EF or know HTTP status codes. Accept ordered positions
   and the explicit gap policy; specify tie handling using the provided IDs.
3. Add request binding, response DTOs, and a scoped query service. Verify U
   belongs to P before querying; an existing person with no events returns
   an empty list, while a missing/foreign person returns 404.
4. Filter by ProjectId, current PersonId, and the half-open time range in SQL.
   Select only IDs/timestamps; do not load properties or query each distinct-ID
   alias separately. Current identity merges intentionally affect these results.
5. Stream or materialize only within the shared row/time budget, then apply
   the pure grouping algorithm. Check cancellation and session count while
   processing; withhold the success response until the complete result fits.
6. Label boundary sessions conservatively even if their first/last event is
   not exactly at the requested boundary. The endpoint cannot know whether
   unseen neighboring events would join the session without additional reads.
7. Checkpoint: move the requested from time inside an existing visit and
   explain the shortened observed duration. Do not label that duration total
   time-on-site or a globally complete session duration.
8. Test equal-threshold splits, just-under-threshold gaps, timestamp ties,
   one event, empty windows, UTC-offset equivalents, and start/end equality.
   Test merged identities using the actual identity service.
9. Test input/session caps and deadline cancellation. Use ordinals scoped to
   the response rather than promising durable session IDs; late events can
   change grouping and ordinals on a later request.
10. Add a visual worked example to the API docs showing the gap rule and
    window-local boundary flags. Keep precomputed sessions and cross-project
    identity stitching outside this first version.

**Acceptance and verification:** The hand-calculated fixture matches exactly;
each included event belongs to one returned session; outside-window events
do not affect grouping; no silent truncation occurs. Run
`dotnet test --filter "FullyQualifiedName~PersonSessionTests|FullyQualifiedName~SessionGroupingTests|FullyQualifiedName~IdentityChainTests"`.

**Rollout and recovery:** Ship as a derived read endpoint with documented
semantics. Compare hand-calculated fixtures before enabling clients; no session
backfill or new table is needed. Changing gap equality later is a behavior
change and needs an explicit contract version or migration of expectations.

**Common mistake:** Presenting the first event within the query window as the
true start of the user's visit when earlier events were never inspected.

**Done when:** Grouping, identity behavior, query boundaries, and resource
limits are explained with exact examples and verified independently.

**Teach back:** Why can the same person's apparent sessions change after an
identity merge or late-arriving event even though no session rows are edited?

<a id="sr-20"></a>
## SR-20 — Receive in-app alerts for event-count thresholds

**User story:** As an analyst, I want an in-app notification when an event's
hourly count reaches a threshold so I can notice significant activity without
repeatedly refreshing a chart.

**Current behavior and learning goal:** Existing queries are caller-driven;
there is no alert scheduler or notification inbox. Add one small alert type
with durable evaluation records and duplicate-free notification creation.
Do not add email, Slack, webhooks, or any external delivery in this story.

**Open:** [QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[Program](../src/Pulse.Api/Program.cs),
[ExportWorker](../src/Pulse.Api/Export/ExportWorker.cs),
[ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs), and
[InsightEndpointsTests](../tests/Pulse.Tests/Api/InsightEndpointsTests.cs).

**Proposed contract and invariant:** Editors manage up to 20 project rules
under `/api/projects/P/alert-rules` with name, exact event name, positive integer
threshold, enabled flag, and revision. Each rule evaluates completed UTC hourly
windows `[start,end)` and triggers when count >= threshold. Persist exactly one
evaluation per rule revision/window and at most one notification for that
evaluation. Readers list project notifications and mark them read for themselves.
New/edited rules begin at the next complete hour after creation/edit and do not
retroactively evaluate older revisions. A restart catches up at most 24 hours;
older missed windows are recorded as skipped. Late events arriving after a window
was evaluated do not trigger reevaluation in v1.

**Implementation steps:**

1. Create `HourlyAlertTests` with a controlled clock and events before, at,
   and after an hour boundary. Draw rule revision, evaluation, notification,
   and per-user read-state records as separate concepts.
2. Add AlertRule, AlertEvaluation, ProjectNotification, and NotificationRead.
   Use unique rule/revision/window-end and evaluation/notification constraints,
   plus notification/user uniqueness for idempotent mark-read operations.
3. Add role- and scope-aware CRUD/list routes with named validation and
   conditional revisions. Require trimmed names/event names of 1–200 characters,
   threshold 1–1,000,000, and bounded list pagination. Deletion marks the rule
   deleted/disabled, preserving existing evaluations and notifications. Users
   cannot mark another user's read state through a body-supplied UserId.
4. Persist the first eligible full window start when a rule is created/edited.
   Example: a 10:15 edit first evaluates 11:00–12:00 after noon. A change exactly
   on an hour may use the hour beginning at that instant; test this explicitly.
5. Add a bounded scheduler/processor: choose at most ten due rule-windows per
   cycle, apply the 24-hour catch-up policy, and process in time order. Store
   a skipped-through watermark rather than creating millions of skipped rows.
6. In each evaluation transaction, verify the rule is still enabled at the
   expected revision, count project/event rows in the half-open window, and
   insert evaluation plus notification when triggered. Conditional progress
   and unique constraints prevent two processors from notifying twice.
7. Store event name, window, observed count, threshold, revision, and evaluatedAt
   in the notification. Do not store arbitrary event properties. The count is
   the data visible at actual evaluation time, not a historical ingestion snapshot.
8. Checkpoint: fail immediately before commit, retry, and assert one evaluation
   and one notification. Fail after commit but before acknowledging progress
   in memory, restart, and prove persisted progress prevents duplication.
9. Add inbox pagination and idempotent mark-read. Project membership is checked
   at read time; removal from the project removes notification visibility.
10. Test equality, no match, empty window, disable/edit races, two processors,
    restart catch-up, late arrivals, and independent read state for two users.
    Document that threshold alerts differ from anomaly detection and that
    skipped history is reported rather than silently treated as evaluated.

**Acceptance and verification:** Exactly-threshold windows notify; below-threshold
windows record nontriggering evaluations; concurrent/restarted processors cannot
duplicate notifications; one user's read action does not affect another. Run
`dotnet test --filter "FullyQualifiedName~HourlyAlertTests|FullyQualifiedName~ProjectRoleTests|FullyQualifiedName~InsightEndpointsTests"`.

**Rollout and recovery:** Migrate tables, deploy management/inbox routes, then
enable the scheduler with a few disabled-by-default rules. Pausing the scheduler
does not delete notifications. Resume obeys the explicit catch-up bound rather
than flooding the inbox with all historical windows.

**Common mistake:** Creating a notification after committing evaluation in a
separate transaction, which can lose delivery on a crash between the two.

**Done when:** Rule timing, evaluation evidence, inbox delivery, and read state
are durable and independently understandable, with no external messaging involved.

**Teach back:** Explain why "evaluated", "triggered", "notification created",
and "read by this user" are four different states.

## Repeat the ideas using different explanations

| Engineering idea | Plain-language explanation | Codebase example | A question to answer without notes |
| --- | --- | --- | --- |
| Invariant | A promise that must survive failures as well as success. | Last admin remains in SR-02. | Which transaction protects that promise? |
| Conditional mutation | Change the record only if it still matches what I inspected. | Flag revision in SR-04. | What belongs in the database predicate? |
| Transaction boundary | The group of writes that become real together. | Audit plus flag update in SR-03. | Which earlier bulk write might sit outside it? |
| Idempotency | Repeating one logical request does not create another admission. | Client event IDs in SR-07. | When does that guarantee expire? |
| Lease and fence | Ownership can expire, and stale owners must be unable to commit. | Project workers in SR-11. | How do you stop a paused old worker? |
| Durable state | Enough information lives in storage to resume after memory is lost. | Export cancellation in SR-12. | What happens after a process crash? |
| Snapshot | Store the values a later operation must keep using. | Copied export rows in SR-13. | Would storing only IDs be sufficient? |
| Lifecycle scope | Say exactly which copies are removed and which remain. | Retention versus erasure. | Can another feature recreate or retain the data? |
| Permission intersection | A request must fit every applicable restriction. | Role plus token scopes in SR-16. | Can a scope ever grant more than membership? |
| Work budget | Stop before claiming an incomplete result is complete. | Bounded trends in SR-18. | Are you limiting input work or just output size? |

Use three passes through a story. On the first, follow the numbered steps with
the code open. On the second, explain the fixture and failure case from memory.
On the third, change one condition—two callers, a restart, a newer revision,
or a deleted identity—and predict the outcome before running the test.

For a review-ready contribution, fill in this record:

```text
Story and concrete user-visible result:
Existing behavior I traced before changing it:
The invariant and where it is enforced:
New schema/contract and compatibility decision:
One successful example with exact expected state:
One controlled failure/race/restart example:
Focused tests discovered and actual results:
Required broader tests and actual results or specific blockers:
Migration, rollout, disablement, and recovery evidence:
Remaining limits I can explain honestly:
```

The goal is to be able to explain why the change remains correct when timing,
data volume, or another caller changes. These plans provide a sequence to
follow; the tests and operational evidence are how you establish the promise.
