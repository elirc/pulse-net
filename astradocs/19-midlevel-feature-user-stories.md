# 30 midlevel feature stories with follow-along implementation plans

**Status: original implementation plans; implementation is now underway.**

Implementation is now underway at the user's request. This document remains
the original contract and follow-along plan; use the
[bootcamp ledger](bootcamp/stories.json) and [journal](bootcamp/journal.md) for
current implementation and verification status.
Routes, DTOs, services, and test classes described as additions below do not
exist yet. Links point to existing code you can inspect before starting.

These stories build on Pulse's current API, SQLite database, ingestion worker,
query engine, and authentication model. They are separate from the
[25 junior stories](18-junior-user-stories.md); completing that backlog is not
a prerequisite. Midlevel practice here means delivering a small feature across
several layers, defining its failure behavior, and explaining its tradeoffs.
The steps are deliberately explicit so you can spend your attention on those
decisions instead of guessing where to begin.

## Choose a feature

| ID | Feature request | What you will learn |
| --- | --- | --- |
| [MID-01](#mid-01) | Duplicate a dashboard while sharing its insights | Copying entities and transaction boundaries |
| [MID-02](#mid-02) | Move dashboards between projects with a template | Versioned contracts and reference mapping |
| [MID-03](#mid-03) | Save several tile positions together | Atomic validation and bulk updates |
| [MID-04](#mid-04) | Refresh selected dashboard tiles | Request-scoped reuse and partial results |
| [MID-05](#mid-05) | Preview an unsaved insight | Shared validation and side-effect-free execution |
| [MID-06](#mid-06) | Edit a saved insight safely | Full replacement contracts and dependent behavior |
| [MID-07](#mid-07) | Find insight usages before deleting it | Relationship queries and guarded deletion |
| [MID-08](#mid-08) | Compare event counts across two periods | Time boundaries and honest comparison math |
| [MID-09](#mid-09) | Chart multiple event series in one request | Bounded orchestration and result alignment |
| [MID-10](#mid-10) | Browse one person's event timeline | Identity relationships and cursor pagination |
| [MID-11](#mid-11) | View a person's activity summary | Aggregation and identity semantics |
| [MID-12](#mid-12) | Preview a cohort before saving it | Reusing a rule evaluator without writes |
| [MID-13](#mid-13) | Replace a dynamic cohort's rules | Validation, current membership, and compatibility |
| [MID-14](#mid-14) | Freeze a dynamic cohort into a static copy | Snapshot semantics and multi-row persistence |
| [MID-15](#mid-15) | Replace a static cohort's member set | Set differences and transactional replacement |
| [MID-16](#mid-16) | Explain a feature-flag decision | Shared execution paths and useful diagnostics |
| [MID-17](#mid-17) | Preview flags for a small audience | Batch reads and request-local caching |
| [MID-18](#mid-18) | Clone a flag as an inactive draft | Unique keys, races, and deterministic rollout |
| [MID-19](#mid-19) | Rotate a project's read key | Credential boundaries and conditional updates |
| [MID-20](#mid-20) | Validate a capture request without enqueueing it | Extracting a shared parser without behavior drift |
| [MID-21](#mid-21) | Explain whether a dead letter can be replayed | Diagnostics and check-versus-action semantics |
| [MID-22](#mid-22) | Replay a selected batch of dead letters | Partial success and per-item transactions |
| [MID-23](#mid-23) | Retry a failed export as a new job | Durable work and immutable failure evidence |
| [MID-24](#mid-24) | Browse export history | Cursor contracts and efficient projections |
| [MID-25](#mid-25) | Delete a finished export and its download | State-aware mutation and race handling |
| [MID-26](#mid-26) | Verify a completed export's download bytes | Encoding, checksums, and HTTP output |
| [MID-27](#mid-27) | See event-name usage over a date range | SQL aggregation and registry-versus-data semantics |
| [MID-28](#mid-28) | Explore the values of an event property | Typed JSON values and bounded computation |
| [MID-29](#mid-29) | Diagnose a project's ingestion health | Derived state and deterministic time tests |
| [MID-30](#mid-30) | Open a project overview with useful counts | Cross-feature composition and consistent definitions |

## How to work through one story

1. Read its user story and example before opening an editor. Restate the
   behavior using a specific caller, input, output, and failure.
2. Open the linked files in order. Draw `HTTP -> access check -> service ->
   database -> response`; label the actual methods you find.
3. Write the acceptance examples as a checklist. Add tests for observable
   behavior, not for whether a private helper was called.
4. Follow one numbered implementation step at a time. A checkpoint means
   stop, inspect the diff, and explain what now works before continuing.
5. Run the story's focused command. Then complete the repository's
   [testing policy](../docs/testing.md), including its full-suite requirement
   for test changes. Record actual results rather than copying expected ones.
6. Update [the API reference](../docs/api-reference.md) with the implemented
   contract and limitations. Only then mark the story done in your own notes.

**Suggested order:** start with MID-01, MID-03, MID-05, and MID-12. MID-06 and
MID-02 reuse MID-05's proposed validator; MID-13 and MID-14 benefit from
MID-12's evaluator extraction. Other stories can be attempted independently.
If a prerequisite is unfinished, implement the named shared piece first in
its own small change. Never assume a proposed helper already exists.

Each story can be split into two or three reviewable changes: contract and
tests, service/endpoint behavior, then documentation and edge cases. Finishing
thirty features is not required to demonstrate midlevel ability; a few features
with clear reasoning and reliable verification are more useful than rushed
completion. Repeat an area until you can explain the path without reading it.

## Shared codebase rules and test recipe

- Start with `git status --short`. This checkout already contains changes;
  preserve them and inspect the relevant diff before editing.
- [Program.cs](../src/Pulse.Api/Program.cs) registers scoped services and maps
  endpoints. New services need registration; a new method in an already
  registered endpoint extension does not need another mapping call.
- [ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs) has
  distinct membership and read-access guards. All proposed routes below are
  **member-only unless explicitly labeled read-access**. Use JWT or a personal
  token for membership; a project write key does not grant management access.
  An unauthenticated management caller gets 401, and an authenticated
  non-member gets 404. Also scope every resource query to the requested project.
- `P`, `D`, `I`, and similar URL placeholders below mean real IDs returned by
  setup requests. They are explanatory notation, not literal route segments.
  Request JSON uses web-style camelCase unless the capture contract says otherwise.
- Validate after the access guard and before writes. Invalid JSON or invalid
  typed route/query values can be rejected by framework binding before your
  handler. Distinguish those failures from your own validation tests.
- These first versions reuse the current schema. Do not add columns merely
  to store a value you can derive. Startup currently calls `EnsureCreated`;
  do not assume it upgrades an existing database if you later expand scope.
- [PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs) uses real SQLite
  and real workers. Create a fresh project per test and fresh users for
  membership tests. [TestAuth](../tests/Pulse.Tests/TestAuth.cs) supplies auth.
  If you capture events, await
  [TestIngestion.WaitForDrainAsync](../tests/Pulse.Tests/TestIngestion.cs)
  before asserting processed data. A drained queue alone does not prove all
  events succeeded; check the expected event rows and failure state too.
- Use explicit UTC timestamps for ranges and ties. For clock-relative tests,
  use a small test `TimeProvider` replacement or construct the service with
  one. Do not use sleeps to manufacture ordering or race conditions.
- Each API story needs a happy path, invalid input, anonymous caller,
  signed-in non-member, and cross-project resource/data test. For a read-access
  story, also test its own read key, a different project's read key, and a write
  key. Use a different project under the same owner to expose missing scoping.
- Use separate test scopes/contexts for concurrent requests. Never run two
  EF operations concurrently on one scoped context. Do not use `Task.WhenAll`
  to speed up loops that share the registered services and their context.
- For a promised atomic write, inspect persisted state after a failure. A
  `SaveChanges` transaction does not automatically include an earlier
  `ExecuteDelete`. Put the complete read/check/write unit inside the intended
  transaction where the story calls for one.
- Cap user-controlled lists before expensive work. For existing query-engine
  methods that materialize a time slice, a shorter date range bounds time,
  not event volume. Record that remaining limitation honestly.
- Each story names a **proposed** test class. For example, create
  `tests/Pulse.Tests/Api/DashboardCopyTests.cs` for MID-01, then run:

  ```powershell
  dotnet test tests/Pulse.Tests/Pulse.Tests.csproj --filter "FullyQualifiedName~DashboardCopyTests"
  ```

  A filter that discovers zero tests is not a passing feature check. Existing
  linked tests are examples and regression targets, not proof that a new
  feature has coverage. The earlier [validation record](../docs/learning/validation.md)
  includes a Windows assembly-loading block and a large-batch timeout; these
  plans neither resolve them nor claim new runtime results.

## Dashboard and saved-insight features

<a id="mid-01"></a>
## MID-01 — Duplicate a dashboard while sharing its insights

**User story:** As an analyst, I want to duplicate a dashboard and rearrange
its tiles so I can build a new view without rebuilding each tile manually.

**Starting point and learning goal:** Dashboards and tiles already have CRUD
routes. A tile references a saved insight; it does not own the query definition.
Practice copying the dashboard/tile records while preserving that distinction.

**Open:** [DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[dashboard contracts](../src/Pulse.Api/Contracts/DashboardContracts.cs),
[DashboardTile](../src/Pulse.Domain/Entities/DashboardTile.cs), and
[DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** `POST /api/projects/P/dashboards/D/duplicate` with
`{"name":"Acquisition experiments"}` returns 201, a Location header, and the
existing `DashboardResponse` shape. Trim the name and require 1–200 characters.
Copy description and layout JSON; generate a new dashboard ID and new tile IDs.
Keep each original insight ID. Limit the operation to 100 tiles; return 409
with a clear detail for an oversized or dangling-reference source. An empty
dashboard is a valid source. No cross-project copy in this story.

**Implementation steps:**

1. Create `DashboardCopyTests` with a source containing two tiles that point
   to one insight. Write the expected IDs before coding: one new dashboard,
   two new tiles, zero new insights.
2. Add `DuplicateDashboardRequest` to the contracts file. Add the route next
   to dashboard creation and call `RequireMemberAsync` first.
3. Add a scoped `DashboardCopyService` in Infrastructure and register it in
   Program. Give it a method accepting project ID, source ID, trimmed name,
   and cancellation token; return a typed outcome for missing/conflicting data.
4. Inside a transaction, load the source with both project and dashboard ID.
   Load at most 101 tiles and validate that every referenced insight belongs
   to the same project. Do not let an inner join silently drop broken tiles.
5. Build new entity instances. Copy only the named fields. Set creation times
   from one `TimeProvider` reading. Read source tiles by CreatedAt then ID for
   deterministic traversal; copied visual placement comes from layout JSON,
   not a promise that newly generated IDs retain source response order.
6. Add all new rows and save once, then commit. Reuse or narrowly extract the
   existing dashboard response mapper. Return the new resource's Location.
7. Checkpoint: move a copied tile and reload both dashboards. Their layouts
   differ, while their insight IDs still match. Document that editing a shared
   insight changes the query used by both dashboards.
8. Add failure tests, run the focused command, and document the size limit and
   shared-insight behavior in the API reference.

**Acceptance and verification:** The two-tile fixture creates exactly three
new rows. Empty sources copy successfully. Whitespace-only and 201-character
names return 400 with no writes. Missing/cross-project sources return 404;
101 tiles or a foreign insight reference return 409 with no partial copy.
A save-failure test at the service level verifies rollback. Run
`dotnet test --filter "FullyQualifiedName~DashboardCopyTests|FullyQualifiedName~DashboardTests"`.

**Common mistake:** Copying the source entity's ID or accidentally creating
new insights because their data appeared in the response DTO.

**Done when:** Copy behavior, row counts, failure atomicity, and shared-query
semantics are demonstrated and documented.

**Teach back:** Explain the difference between copying a shortcut and copying
the file it points to. Map both parts of the analogy to actual entity IDs.

<a id="mid-02"></a>
## MID-02 — Move dashboards between projects with a template

**User story:** As a team member, I want to export a reusable dashboard template
and import it into another project so teams can share a useful starting layout.

**Starting point and learning goal:** Existing dashboard responses contain
database IDs. Design a portable document whose references are local to the
document. **Prerequisite:** MID-05's proposed insight configuration validator.

**Open:** [DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[Insight](../src/Pulse.Domain/Entities/Insight.cs),
[dashboard contracts](../src/Pulse.Api/Contracts/DashboardContracts.cs),
[FilterContracts](../src/Pulse.Api/Contracts/FilterContracts.cs), and
[DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** Member-only `GET /api/projects/P/dashboards/D/template`
returns `{version:1,name,description,insights:[{ref,name,type,config}],
tiles:[{insightRef,layout}]}`. `POST /api/projects/P/dashboards/import` accepts
that document and returns 201 with a new `DashboardResponse`. `ref` is a
case-sensitive document-local string such as `query-1`, never a database ID.
Allow at most 50 insights and 100 tiles; cap request bytes at 256 KiB. Reject
unknown versions, duplicate refs, unreferenced insight definitions, dangling
tile refs, and cohort-targeted filters with 400. Cohort IDs are project-local
and deliberately unsupported in portable version 1. Person-property and event
filters keep their existing semantics. Export of an unsupported source returns 409.

**Implementation steps:**

1. Create `DashboardTemplateTests`. Draw an example with two tiles sharing
   one insight; the document should contain one insight definition.
2. Define template DTOs separately from entity and response DTOs. State name
   limits of 1–200 trimmed characters and description maximum 2,000 characters.
   Require object layouts and object configurations.
3. Add a scoped template service and the two routes. Authorize the source for
   export and the destination for import; importing a file needs no access
   to the project that originally produced it.
4. Implement export by loading project-scoped source rows, checking all
   references, assigning stable refs in a deterministic order, and projecting
   only template fields. Never serialize entire entities.
5. For import, enforce the body limit before expensive parsing. Validate the
   entire graph and every insight using MID-05's validator. Store its validated
   storage config, preserving relative omissions rather than resolved preview
   dates. Walk parsed filters to reject cohort dependencies; do not search raw
   JSON text. Apply the same supported-config checks during export.
6. Build a `ref -> new Insight` map, then build new tile entities using that
   map. Preserve sharing within the import, generate every database ID anew,
   and persist insights, dashboard, and tiles in one transaction.
7. Checkpoint: export from A, import into B, and refresh B. Add matching test
   events to each project so you can prove the imported queries use B's data.
8. Add malformed-document and rollback cases. Document version 1's limits,
   explicit date ranges remaining explicit, and the new-ID behavior.

**Acceptance and verification:** A round trip preserves names, configs,
layouts, and shared references without preserving database IDs. Reimporting
creates another independent set. Unknown version, duplicate ref, foreign cohort
filter, excessive body, and invalid config create no rows; oversized bodies
return 413. Missing export source returns 404. Run
`dotnet test --filter "FullyQualifiedName~DashboardTemplateTests|FullyQualifiedName~DashboardTests"`.

**Common mistake:** Treating an exported GUID as a valid reference in another
project, or leaking project credentials through a convenient response object.

**Done when:** A fixture moves between two projects, every reference is remapped,
and invalid imports leave the destination unchanged.

**Teach back:** Why does a portable template need both a version and its own
reference names? Explain the difference between schema and stored data.

<a id="mid-03"></a>
## MID-03 — Save several tile positions together

**User story:** As a dashboard user, I want to save all dragged tile positions
in one request so a failed save cannot leave half my dashboard rearranged.

**Starting point and learning goal:** The current tile update changes one
layout JSON object. Add an explicitly atomic multi-tile operation while keeping
that existing route compatible. Practice validating a collection before mutation.

**Open:** [DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[dashboard contracts](../src/Pulse.Api/Contracts/DashboardContracts.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** `PUT /api/projects/P/dashboards/D/tile-layouts` accepts
`{"tiles":[{"tileId":"GUID","layout":{"x":0,"y":2,"w":6,"h":3}}]}`.
Allow 1–100 unique tile IDs. Each layout must be an object with integer
`x,y,w,h`; require `0 <= x < 12`, `y >= 0`, `1 <= w <= 12`, `h >= 1`,
`x+w <= 12`, and `y,h <= 10000`. Preserve additional layout fields.
Success returns 200 with the updated dashboard. Invalid layouts or duplicate
IDs return 400. Any tile outside the requested dashboard returns a generic 404.
Tiles absent from the request keep their layouts. Overlap is permitted.

**Implementation steps:**

1. Create `DashboardLayoutBatchTests` with three tiles. Request changes for
   two, then include one invalid layout and assert that all three remain old.
2. Add request records with nullable layout values so missing objects can be
   reported clearly. Add a small pure layout validator in the API layer;
   return indexed errors such as `tiles[1].layout.w`.
3. Add the route and membership guard. Validate collection size, duplicates,
   JSON shape, and all coordinates before assigning any entity property.
4. Start a transaction and look up the dashboard by project and ID. Fetch its
   requested tiles in one query; compare the retrieved ID set with the input
   set. Do not independently search each ID without a dashboard predicate.
5. Build normalized layout strings in a temporary map. Checkpoint: no database
   rows should have been changed yet, even though every input is validated.
6. Assign the validated strings, save once, and commit. Reuse the existing
   dashboard response shape. Pass cancellation through every database call.
7. State the concurrency policy: overlapping successful requests use the last
   committed layout for a tile; this feature does not add optimistic version
   checks. Atomicity within one request is still required.
8. Add boundary cases, inspect saved layouts, run the focused tests, and
   document the distinction between this strict grid contract and the existing
   single-tile route's more general JSON contract.

**Acceptance and verification:** Two valid changes appear together, the third
tile stays unchanged, and additional fields survive. Test `x+w=12`, `x+w=13`,
fractional numbers, missing fields, duplicates, zero/101 items, a tile from
another dashboard in the same project, and a foreign-project tile. All rejected
requests leave every requested layout unchanged. Run
`dotnet test --filter "FullyQualifiedName~DashboardLayoutBatchTests|FullyQualifiedName~DashboardTests"`.

**Common mistake:** Saving inside the input loop, or using input validation as
a substitute for verifying that every tile belongs to the dashboard.

**Done when:** The success and mixed-validity cases prove the all-or-nothing
contract, and existing single-tile tests still pass.

**Teach back:** Explain why a batch containing nine good items and one bad item
can reasonably save zero items. Which product promise requires that behavior?

<a id="mid-04"></a>
## MID-04 — Refresh selected dashboard tiles

**User story:** As a dashboard user, I want to refresh only the tiles I am
viewing so I can retrieve their results without rerunning unrelated queries.

**Starting point and learning goal:** The existing refresh handler runs every
tile sequentially and returns per-tile results/errors. Add a selected refresh
route and reuse repeated insight results within that request.

**Open:** [DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[InsightRunnerService](../src/Pulse.Infrastructure/Services/InsightRunnerService.cs),
[dashboard contracts](../src/Pulse.Api/Contracts/DashboardContracts.cs), and
[DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** `POST /api/projects/P/dashboards/D/refresh-selection`
with `{"tileIds":["GUID-A","GUID-B"]}` returns the existing
`DashboardRefreshResponse` containing only those tiles, in request order.
Require 1–50 unique IDs; duplicates return 400. Missing/cross-dashboard IDs
return 404 before any query runs. A bad stored insight config produces its
existing per-tile error while valid tiles still return results. Tiles sharing
one insight reuse one execution result within this response. No persistent cache.

**Implementation steps:**

1. Create `DashboardSelectedRefreshTests` with three tiles: two share a valid
   insight and one uses an invalid stored configuration. Record each tile's ID.
2. Add the selection request contract and route. Authorize, validate the ID
   list, and resolve the dashboard and complete selected tile set.
3. Load referenced insights with an explicit project predicate. Detect missing
   references instead of letting a join erase a tile. Represent a broken
   insight reference as a per-tile error after tile ownership is validated.
4. Extract only the refresh orchestration that the two routes truly share,
   or keep a small selection service. Avoid a repository-wide endpoint rewrite.
5. Iterate in requested order and maintain a local dictionary from insight ID
   to `InsightRunResult`. Execute the runner sequentially on the first encounter;
   reuse its result and error on later encounters.
6. Keep tile IDs and layout JSON unique in each response entry even when the
   result object is reused. Set `RefreshedAt` from the injected clock.
7. Checkpoint: select just the valid tiles and verify the broken tile is absent.
   Select the broken tile too and verify the response remains 200 with its error.
8. Add a narrow runner seam or test instrumentation to count executions if
   needed for the reuse promise. Keep HTTP behavior tests against the real
   runner; do not replace all integration coverage with mocks.
9. Run tests and document order, per-tile failure behavior, and the fact that
   a later request computes results again from current data.

**Acceptance and verification:** Input order is preserved; unselected tiles
are absent; a shared insight executes once in one request and again in a new
request. Invalid selection performs no query work. Cancellation is propagated,
not converted into a successful tile error. Run
`dotnet test --filter "FullyQualifiedName~DashboardSelectedRefreshTests|FullyQualifiedName~DashboardTests"`.

**Common mistake:** Using parallel tasks with the shared scoped DbContext, or
caching results across requests without an invalidation rule.

**Done when:** Selection, ownership checks, result reuse, and partial query
failures are verified without changing full-dashboard refresh behavior.

**Teach back:** Explain why two tiles can share a query result but still need
two response entries. Where does the cache begin and end?

<a id="mid-05"></a>
## MID-05 — Preview an unsaved insight

**User story:** As an analyst, I want to try an insight configuration before
saving it so I can correct mistakes without filling the saved-insight list.

**Starting point and learning goal:** `InsightRunnerService` parses stored JSON
and dispatches to `QueryService`. Saved creation currently accepts object JSON
without fully validating its query semantics. Extract a reusable configuration
validator for this feature and dependent stories without silently changing
legacy saved-query behavior.

**Open:** [InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[InsightRunnerService](../src/Pulse.Infrastructure/Services/InsightRunnerService.cs),
[insight contracts](../src/Pulse.Api/Contracts/InsightContracts.cs),
[PropertyFilterParser](../src/Pulse.Domain/PropertyFilterParser.cs), and
[InsightEndpointsTests](../tests/Pulse.Tests/Api/InsightEndpointsTests.cs).

**Proposed contract:** `POST /api/projects/P/insights/preview` accepts
`{"type":"trend","config":{"event":"signup","interval":"day"}}`.
Return 200 `{type,result}` for a valid query; invalid configuration returns 400
with field errors. Allow only named trend/funnel/retention types, object config,
and at most 32 KiB of UTF-8 config JSON. No name or persistence is required.
Retain the runner's defaults but reject explicitly invalid typed values instead
of replacing them with defaults. Trend/funnel ranges must satisfy `from <= to`
and span at most 90 days; retention `days` stays 1–60.

**Implementation steps:**

1. Create `InsightPreviewTests`. Seed three signup events at explicit times
   and write a preview assertion plus a count assertion showing zero new insights.
2. Read each runner branch and make a validation table: trend event and
   hour/day/week interval; funnel at least two nonblank string steps and
   windowDays 1–90; retention valid DateOnly from and days 1–60. Limit preview
   funnels to 20 steps and trim event names; require 1–200 characters per name.
3. Add the preview DTO and a proposed Infrastructure `InsightConfigValidator`.
   Accept a captured `now` so relative defaults resolve once. Return errors,
   validated storage config preserving omitted relative dates, and resolved
   run config for this execution. Keep HTTP response construction outside the
   validator; storage and execution need different representations of defaults.
4. Reuse `PropertyFilterParser` for supplied filters and its existing target
   semantics. Reject non-array filters, wrong numeric types, unknown named
   type/interval values, invalid dates, and reversed ranges explicitly.
5. Add the member-only route. Validate before running anything, then construct
   an untracked temporary `Insight` carrying the requested project and normalized
   config. Pass it to the existing runner; never add it to `db.Insights`.
6. Return the runner result in the new wrapper. Preserve its cancellation
   behavior. A validation failure becomes a validation problem; unexpected
   storage failures remain failures rather than a fabricated empty chart.
7. Checkpoint: compare a valid preview with a saved equivalent run through the
   export/refresh path, using explicit dates so defaults cannot hide differences.
8. Add validation and no-write tests. Document that large event volumes can
   still be expensive because the current query engine materializes slices.

**Acceptance and verification:** Valid trend, funnel, and retention examples
match their existing query semantics. Missing versus invalid values have
distinct outcomes; empty data produces valid empty/zero results. Invalid filters,
numeric enum strings, 91-day ranges, and oversized configs return 400 with no
new rows. Run
`dotnet test --filter "FullyQualifiedName~InsightPreviewTests|FullyQualifiedName~InsightEndpointsTests"`.

**Common mistake:** Saving a temporary insight and deleting it afterward, which
creates writes and failure windows in an operation promised to be read-only.

**Done when:** Preview works for all three types and the shared validator is
documented for MID-02 and MID-06 to reuse.

**Teach back:** Explain validation, normalization, and execution as three
different jobs. Which one decides that an omitted date means a default?

<a id="mid-06"></a>
## MID-06 — Edit a saved insight safely

**User story:** As an analyst, I want to replace a saved insight's configuration
so dashboards using it reflect my corrected query without replacing their tiles.

**Starting point and learning goal:** Saved insights currently support create,
list, and get. Add an update contract with explicit replacement semantics.
**Prerequisite:** MID-05's configuration validator and its behavior table.

**Open:** [InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[insight contracts](../src/Pulse.Api/Contracts/InsightContracts.cs),
[Insight](../src/Pulse.Domain/Entities/Insight.cs),
[InsightRunnerService](../src/Pulse.Infrastructure/Services/InsightRunnerService.cs),
and [DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** `PUT /api/projects/P/insights/I` accepts
`{"name":"Weekly signups","type":"trend","config":{"event":"signup"}}`
and returns 200 `InsightResponse`. Require all three fields. Trim name to
1–200 characters and apply MID-05's supported configuration limits. Replace
the entire config; omitted old keys disappear. Keep ID, ProjectId, and CreatedAt.
Type may change when the new config is valid for the new type. Missing/foreign
insights return 404. This first version uses last successful write wins.

**Implementation steps:**

1. Create `InsightUpdateTests` with an insight used by two dashboard tiles.
   Save an initial event name and explicit date range so before/after results
   can be compared exactly.
2. Define `ReplaceInsightRequest`; do not reuse a nullable partial-update DTO
   whose omission rules would make full replacement ambiguous.
3. Add the route and membership check, then load by project and insight ID.
   Build a candidate name, type, and normalized configuration in local values.
4. Run all validation before assigning any tracked entity field. Return the
   field errors together so correcting a name does not require another request
   just to discover an invalid interval.
5. Store the validator's validated storage config, retaining relative omissions
   so future refreshes remain relative. Use normalized explicit bounds only
   for preview execution. Add a test distinguishing these two validator outputs
   so the update cannot accidentally persist a preview's resolved date range.
6. Save once and return the existing response mapper. Do not create new tiles,
   query results, or exports. Existing queued insight exports continue to use
   whatever saved configuration the current processor reads when they execute.
7. Checkpoint: refresh both dashboards and verify they use the new config.
   Also verify a previously completed export's stored download is unchanged.
8. Add replacement, type-change, validation, and membership tests. Document
   shared-insight effects and the last-write policy in the API reference.

**Acceptance and verification:** The insight retains identity/timestamps; old
config keys disappear; both referencing tiles use the replacement. An invalid
config plus a valid name changes neither. A relative default stays relative
across later runs with a controlled clock. Cross-project IDs return 404. Run
`dotnet test --filter "FullyQualifiedName~InsightUpdateTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Persisting a preview's resolved "now" date and accidentally
freezing a saved query that should continue to show a rolling period.

**Done when:** Replacement and shared-use behavior are proven, and the preview
validator supports both storage and execution without conflicting defaults.

**Teach back:** Why is editing a saved query different from editing a completed
export? Describe what each record actually stores.

<a id="mid-07"></a>
## MID-07 — Find insight usages before deleting it

**User story:** As an analyst, I want to see which dashboards use an insight
and delete it only when unused so cleanup does not leave broken dashboard tiles.

**Starting point and learning goal:** Tiles store `InsightId`, while saved
insights have no delete route. The model does not configure a relationship that
automatically provides the proposed restriction. Implement a visible usage
check and enforce the same invariant during deletion.

**Open:** [InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[DashboardEndpoints](../src/Pulse.Api/Endpoints/DashboardEndpoints.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs),
[ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs),
and [DashboardTests](../tests/Pulse.Tests/Api/DashboardTests.cs).

**Proposed contract:** `GET /api/projects/P/insights/I/usages` returns
`{insightId,dashboardCount,tileCount,dashboards:[{id,name,tileCount}]}` ordered
by dashboard name then ID, capped at 100 dashboard entries with a `truncated`
boolean. `DELETE /api/projects/P/insights/I` returns 204 only if no tile uses it;
otherwise 409 with counts. Missing/foreign insight returns 404. Deletion does
not inspect historical export JSON: completed downloads remain available;
pending insight exports may fail if their source is deleted before execution.

**Implementation steps:**

1. Create `InsightUsageTests`: two tiles in dashboard A and one in dashboard B
   reference I. Expect two dashboards and three tiles, then a blocked delete.
2. Add usage DTOs and a query service method. Scope the insight first, then
   join tiles to dashboards belonging to P. Count all usages before limiting
   the detailed list so the summary remains accurate.
3. Add the member-only usage route. An existing unused insight returns zero
   counts and an empty list; it is different from a missing insight.
4. Add delete inside a transaction covering the existence check, usage check,
   and removal. Do not implement it as a client-side GET followed by an
   unconditional delete; usage can change between requests.
5. Inspect every tile-creation path. Put its insight existence check and tile
   insertion inside a transaction too, including proposed copy/import paths
   if already implemented. Preserve validation and response behavior.
6. Define bounded contention handling for SQLite: an aborted transaction must
   leave no partial state; if retrying, retry the whole operation with a fresh
   context, never just the final save using stale checks. Do not claim the
   usage GET itself reserves the insight for later deletion.
7. Checkpoint: remove all referencing tiles, delete I, and verify its detail
   route is 404 while a previously completed export still downloads.
8. Test a controlled add-tile/delete interleaving using separate contexts.
   Accept a conflict/retry failure for one operation; never a committed tile
   pointing at a successfully deleted insight. Document the export limitation.

**Acceptance and verification:** Counts handle repeated tiles correctly; the
list contains only P's dashboards. A blocked delete preserves the insight and
tiles. A successful delete removes only the insight. The race preserves the
reference invariant. Run
`dotnet test --filter "FullyQualifiedName~InsightUsageTests|FullyQualifiedName~DashboardTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Thinking a preflight usage response guarantees that a later
delete will be safe, or assuming a GUID property creates a foreign key by itself.

**Done when:** Usage reporting and server-enforced deletion agree, including
under the tested competing-write scenario.

**Teach back:** Explain "check then act" with a dashboard tile appearing between
two requests. Which checks must share a transaction?

## Analytics and person features

<a id="mid-08"></a>
## MID-08 — Compare event counts across two periods

**User story:** As an analyst, I want current and previous period event counts
with a percentage change so I can see whether an event is becoming more common.

**Starting point and learning goal:** `QueryService.TrendAsync` provides buckets
for one inclusive range. Add a separate comparison query with explicit
non-overlapping half-open ranges; do not alter existing trend boundaries.

**Open:** [InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[insight contracts](../src/Pulse.Api/Contracts/InsightContracts.cs),
[DateTimeOffsetConversionTests](../tests/Pulse.Tests/Infrastructure/DateTimeOffsetConversionTests.cs),
and [InsightEndpointsTests](../tests/Pulse.Tests/Api/InsightEndpointsTests.cs).

**Proposed contract, read-access:** `GET /api/projects/P/insights/period-comparison?event=signup&from=2026-03-08T00:00:00Z&to=2026-03-15T00:00:00Z`
returns current and previous bounds, `currentCount`, `previousCount`, `delta`,
and nullable `percentChange`. Current is `[from,to)`; previous has equal duration
and ends at `from`. Require event length 1–200 and explicit valid bounds with
`0 < duration <= 90 days`. No property filters or unique-person metric in v1.
For counts 15 and 10, return delta 5 and percentChange 50. If previous is zero,
return null for percentChange, even when current is also zero.

**Implementation steps:**

1. Create `PeriodComparisonTests` using explicit timestamps just before, at,
   and just after each boundary. Calculate expected counts on paper first.
2. Add a response DTO containing all four actual bounds so callers do not
   need to guess what "previous" means. Use long counters and a decimal or
   double percentage with documented rounding to two decimal places.
3. Add the read-access route and input validation. Compute duration and the
   previous start once; reject date arithmetic outside the supported timestamp
   range with 400 instead of letting an overflow become 500.
4. Add a comparison method in a focused Infrastructure service. Build a
   project/event-scoped SQL query spanning both periods, then compute counts
   using conditional aggregation or two sequential scoped count queries.
5. Keep counting in SQL; do not load all event JSON or call TrendAsync and
   accidentally inherit inclusive end points. This metric counts event rows,
   including events whose PersonId is null.
6. Calculate delta and percentage using non-integer division. Do not report
   infinity or invent a 100% increase from a zero baseline.
7. Checkpoint: put one event exactly at `from`; it belongs only to current.
   Put one exactly at `to`; it belongs to neither queried period.
8. Add auth, empty-period, boundary, and project-isolation cases. Document that
   this endpoint's half-open intervals differ from existing inclusive trends.

**Acceptance and verification:** The 15/10 fixture produces 5 and 50; 0/0
produces delta 0 and null percentage; 0/10 produces -100%. Test equal/reversed
bounds, time-zone-equivalent timestamps, and an underflowing previous start.
Run `dotnet test --filter "FullyQualifiedName~PeriodComparisonTests|FullyQualifiedName~InsightEndpointsTests"`.

**Common mistake:** Including the shared boundary in both periods or using
integer division and losing fractional percentage changes.

**Done when:** Each event boundary has one documented interpretation and the
comparison math is verified with exact fixtures.

**Teach back:** Explain why changing from zero to five events has no finite
percentage increase under this formula. What useful values can still be shown?

<a id="mid-09"></a>
## MID-09 — Chart multiple event series in one request

**User story:** As an analyst, I want several named event trends in one response
so my chart can compare signup, activation, and purchase over the same window.

**Starting point and learning goal:** Existing trend queries handle one event.
Compose that tested behavior into a bounded multi-series endpoint and define
alignment explicitly instead of building a second bucketing algorithm.

**Open:** [QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[TimeBucket](../src/Pulse.Domain/TimeBucket.cs),
[InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs),
[insight contracts](../src/Pulse.Api/Contracts/InsightContracts.cs), and
[QueryEdgeCaseTests](../tests/Pulse.Tests/Api/QueryEdgeCaseTests.cs).

**Proposed contract, read-access:** `POST /api/projects/P/insights/multi-trend`
accepts `{events:["signup","purchase"],from,to,interval:"day"}` and returns
`{from,to,interval,series:[{event,buckets}],annotations}`. Require explicit
bounds with `from <= to`, at most 90 days, and 1–5 unique trimmed event names
of 1–200 characters. Names are case-sensitive. Reject duplicates with 400.
Reuse existing inclusive ranges, hour/day/week buckets, and unique-person
semantics. No breakdown or filters in this first version.

**Implementation steps:**

1. Create `MultiTrendTests` with three signup events, one purchase event, and
   one requested event that has no rows. Include an annotation in the range.
2. Add request/response DTOs. Make the response event list preserve request
   order rather than dictionary iteration or total-count order.
3. Add a validator for names, list size, named interval, and explicit bounds.
   Validate the whole request before executing any query. Reject numeric enum
   aliases rather than accepting them as interval names.
4. Add the read-access route and a small orchestration method. Resolve the
   common range once and call `TrendAsync` sequentially for each event.
5. Project each result into its series entry. Include annotations once at the
   top level. Keep bucket starts exactly as the existing engine produced them;
   do not shift them to match an arbitrary client timezone.
6. Checkpoint: compare each returned series with a standalone existing trend
   request using identical explicit inputs. They should match bucket for bucket.
7. Test empty-series zero filling and equal bucket starts across series. Explain
   that per-bucket unique persons are not additive across event series; one
   person may occur in several series.
8. Run focused and existing query tests. Document the five-series cap, no
   cross-query database snapshot promise, and the inherited cost of loading
   each matching time slice. Optimize only with measured evidence later.

**Acceptance and verification:** Order and bucket boundaries match inputs;
missing events still get the engine's zero-count buckets. An invalid fifth
event rejects the whole request. Own-project read keys work, write keys do not.
Annotation IDs appear once in the top-level list. Run
`dotnet test --filter "FullyQualifiedName~MultiTrendTests|FullyQualifiedName~QueryEdgeCaseTests"`.

**Common mistake:** Parallelizing existing scoped services or summing unique
person counts and labeling the result as unique people across all events.

**Done when:** The composed endpoint agrees with standalone trends and its
limits and consistency behavior are documented.

**Teach back:** Explain why reusing a query service can improve semantic
consistency even when it is not the fastest possible implementation.

<a id="mid-10"></a>
## MID-10 — Browse one person's event timeline

**User story:** As a support engineer, I want to page through a person's events
so I can understand what happened before and after a reported problem.

**Starting point and learning goal:** Person detail exposes identity mappings,
and exports already implement timestamp/ID cursors. Connect those concepts
using the canonical PersonId stored on events rather than one distinct ID.

**Open:** [PersonEndpoints](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[ExportService](../src/Pulse.Infrastructure/Services/ExportService.cs),
[IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs),
[AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs),
and [IdentityChainTests](../tests/Pulse.Tests/Api/IdentityChainTests.cs).

**Proposed contract:** `GET /api/projects/P/persons/U/events?limit=50&cursor=...`
returns `{events:[{id,event,timestamp,distinctId,properties}],nextCursor}`.
Use newest-first `(Timestamp,Id)` order. Default limit 50; reject outside 1–200.
An optional exact `event` name is trimmed and limited to 200 characters.
Only rows with both ProjectId=P and PersonId=U qualify. Missing or foreign
person returns 404. A cursor is a position, not a snapshot or authorization token.

**Implementation steps:**

1. Create `PersonTimelineTests`. Seed tied timestamps with explicit IDs and
   events captured under two identities that the identity service merges.
2. Add the response contracts and member-only route. Resolve U in P before
   returning an empty timeline; absence is different from no activity.
3. Add a service method with the project/person/event predicates in SQL.
   Read the export cursor example, but implement descending continuation:
   timestamp less than the cursor, or equal timestamp and ID less than it.
4. Add a small versioned cursor codec with a timeline purpose marker, project
   ID, person ID, event filter, and position. Treat it as untrusted input;
   reject malformed/oversized cursors, wrong context, and out-of-range ticks
   with 400. Encode as URL-safe text and cap input at 2,048 characters.
5. Use matching SQL ordering and continuation comparisons, then take limit+1.
   Return at most limit rows and base the next cursor on the last returned row
   only when another row exists. Test ordering against real SQLite.
6. Project JSON properties after paging. Do not query every alias separately,
   which risks duplicates and missing merged historical events.
7. Checkpoint: traverse three pages containing timestamp ties. Every fixture
   event should appear exactly once when the data is unchanged.
8. Add malformed-cursor, merge, isolation, and empty-page tests. Document that
   events inserted later ahead of the cursor require restarting from page one.

**Acceptance and verification:** Events under both merged identities appear;
another person's events do not. Three tied events survive page size one without
duplicates or omissions. A reused cursor for another person/filter is 400.
An empty existing person has `events:[]` and null cursor. Run
`dotnet test --filter "FullyQualifiedName~PersonTimelineTests|FullyQualifiedName~IdentityChainTests"`.

**Common mistake:** Copying the export's ascending comparison while reversing
only the ORDER BY, or assuming Base64 makes a cursor trustworthy.

**Done when:** Identity-aware paging is correct under ties and its live-data
limitations are explicit.

**Teach back:** Why is a timestamp alone insufficient as a cursor? Draw three
events with the same timestamp and walk through page size one.

<a id="mid-11"></a>
## MID-11 — View a person's activity summary

**User story:** As a support engineer, I want a person's event count, active
days, and most-used event names over a chosen period so I can understand their
activity without paging through every event.

**Starting point and learning goal:** Person records, event rows, and aliases
are separate. Add a summary whose count definitions are precise and whose
database queries avoid loading full property documents.

**Open:** [PersonEndpoints](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[Person](../src/Pulse.Domain/Entities/Person.cs),
[AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs),
[TimeBucket](../src/Pulse.Domain/TimeBucket.cs), and
[IdentityTests](../tests/Pulse.Tests/Api/IdentityTests.cs).

**Proposed contract:** `GET /api/projects/P/persons/U/activity-summary?from=...&to=...`
returns `{personId,from,to,totalEvents,activeUtcDays,firstEventAt,lastEventAt,
topEvents:[{event,count}]}`. Require a nonempty half-open range `[from,to)` of
at most 90 days. Return up to five event names ordered by count descending,
then ordinal name. No events means zero counts, null first/last, and an empty
top list. This describes processed events linked to U, not account age,
distinct IDs, or queued events. MID-10 is useful reading but not required.

**Implementation steps:**

1. Create `PersonActivitySummaryTests`: two events on one UTC day and one
   event on the next, including two aliases resolved to the same person.
2. Add the response DTO and a small summary service. Verify person existence
   with both project and person ID before aggregating its events.
3. Validate the range and document exclusive `to`. Build one reusable scoped
   event query and derive scalar count/min/max and grouped name counts from it.
4. Compute active UTC days from the stored UTC-tick representation. Prefer
   a tested SQL projection if it translates; otherwise load only timestamps
   for this person's bounded range and distinct their UTC dates in memory.
   Record the cost of that fallback instead of calling it constant-memory.
5. Execute the queries sequentially. Use nullable min/max projections so an
   empty set does not throw or return a fabricated minimum timestamp.
6. Apply deterministic top-five ordering, explicitly handling equal counts.
   Keep person properties and raw event JSON out of the summary response.
7. Checkpoint: compare totalEvents with the fixture rows, not the number of
   identities. A person's CreatedAt should not influence any activity metric.
8. Add timezone-boundary and no-data cases, then document that several reads
   can observe ongoing ingestion at slightly different moments; v1 is a live
   summary rather than a transactionally frozen analytics report.

**Acceptance and verification:** The three-event fixture has two active UTC
days. An offset timestamp crossing local midnight is assigned by UTC date.
An event exactly at `to` is excluded; null-PersonId and foreign-project events
are excluded. Empty users and unknown users have distinct results. Run
`dotnet test --filter "FullyQualifiedName~PersonActivitySummaryTests|FullyQualifiedName~IdentityTests"`.

**Common mistake:** Counting aliases as people, or using local machine dates
to group a dataset whose timestamps are normalized to UTC.

**Done when:** Each metric has a fixture-backed definition and the query cost
and live-read consistency are described.

**Teach back:** A person has three distinct IDs and ten events on two days.
Explain why none of those numbers is interchangeable.

## Cohort features

<a id="mid-12"></a>
## MID-12 — Preview a cohort before saving it

**User story:** As an analyst, I want to preview how many people match proposed
cohort rules so I can refine an audience before creating a saved cohort.

**Starting point and learning goal:** `CohortService` evaluates stored static
or dynamic cohorts. Extract dynamic rule evaluation into a reusable method
that accepts project, parsed rules, and one evaluation time without storing a row.

**Open:** [CohortEndpoints](../src/Pulse.Api/Endpoints/CohortEndpoints.cs),
[CohortService](../src/Pulse.Infrastructure/Services/CohortService.cs),
[CohortRules](../src/Pulse.Domain/CohortRules.cs),
[cohort contracts](../src/Pulse.Api/Contracts/CohortContracts.cs), and
[CohortTests](../tests/Pulse.Tests/Api/CohortTests.cs).

**Proposed contract:** `POST /api/projects/P/cohorts/preview` accepts
`{rules:[{kind:"property",property:"plan",operator:"equals",value:"pro"}],
sampleLimit:10}`. Return `{evaluatedAt,count,samplePersonIds}`. Require 1–10
rules and sampleLimit 1–50, default 10; reuse existing parser semantics.
Count all matching people and return the first sampleLimit IDs under a defined
deterministic GUID ordering. Reject over 32 KiB of rules JSON with 400.
The sample is deterministic, not a statistically random sample.

**Implementation steps:**

1. Create `CohortPreviewTests` with pro/basic people and performed-event rules.
   Write expected AND intersections by listing the matching person IDs manually.
2. Define the preview request and response. Add the membership guard and
   validation before invoking evaluation. Empty rules must not mean "everyone".
3. Refactor the private dynamic evaluator to accept parsed rules and an
   evaluation timestamp. Let the existing stored-cohort method parse its
   existing JSON and delegate to this shared path.
4. Capture `TimeProvider.GetUtcNow()` once per evaluation and pass it through
   behavioral rules. Preserve current lower-bound and future-event semantics;
   this extraction must not quietly alter which events existing cohorts match.
5. Evaluate the request directly through that method. Keep database access
   project-scoped and sequential, and retain the rule intersection behavior.
6. Sort IDs once, compute total count before sampling, and return the bounded
   sample. Never insert a temporary Cohort or CohortPerson row.
7. Checkpoint: save an equivalent cohort and compare its current member set
   with preview under the same controlled clock and unchanged data.
8. Add existing-cohort regression cases, parser errors, and no-write assertions.
   Document that limiting returned IDs does not limit evaluator work: property
   rules currently inspect the project's person properties in memory.

**Acceptance and verification:** Preview count equals the equivalent stored
cohort's count, with a bounded deterministic sample. Rules AND together;
unknown event names produce zero matching members. Invalid input and valid
preview both leave cohort tables unchanged. Run
`dotnet test --filter "FullyQualifiedName~CohortPreviewTests|FullyQualifiedName~CohortTests|FullyQualifiedName~CohortRuleParserTests"`.

**Common mistake:** Applying sampleLimit before intersecting rule results, which
can hide people who should match the complete rule set.

**Done when:** Stored and unsaved evaluation share one path and preview's count,
sample, and performance meaning are explicit.

**Teach back:** Explain why "return only ten IDs" does not necessarily mean
"evaluate only ten people." Where is the expensive work performed?

<a id="mid-13"></a>
## MID-13 — Replace a dynamic cohort's rules

**User story:** As an analyst, I want to update an existing dynamic cohort's
rules so flags and queries that reference the cohort use the revised audience.

**Starting point and learning goal:** Cohort creation validates rules, but
there is no rules-update route. Keep the cohort identity stable while validating
and replacing its definition. MID-12's shared evaluator is helpful but optional.

**Open:** [CohortEndpoints](../src/Pulse.Api/Endpoints/CohortEndpoints.cs),
[CohortService](../src/Pulse.Infrastructure/Services/CohortService.cs),
[CohortRules](../src/Pulse.Domain/CohortRules.cs),
[FeatureFlagService](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs),
and [CohortTests](../tests/Pulse.Tests/Api/CohortTests.cs).

**Proposed contract:** `PUT /api/projects/P/cohorts/C/rules` accepts
`{"rules":[{"kind":"property","property":"plan","operator":"equals","value":"pro"}]}`
and returns 200 `CohortResponse`. Require 1–10 valid rules and at most 32 KiB
of UTF-8 rules JSON. Missing/foreign cohort returns 404; static cohorts return 409.
Replace the full rule array. Name, ID, type, and CreatedAt stay the same.
No membership rows are materialized for a dynamic cohort.

**Implementation steps:**

1. Create `CohortRuleUpdateTests` with pro and basic people. Create a dynamic
   basic cohort and a flag targeting it, with rollout 100% to isolate targeting.
2. Add `ReplaceCohortRulesRequest` and a member-only route. Load the cohort
   using project and cohort ID before reporting whether its type is eligible.
3. Share the new route's rules validation with preview if MID-12 exists.
   Retain `CohortRuleParser` as the source of operator/kind semantics; do not
   invent another JSON rule language in the endpoint.
4. Validate the complete candidate array and byte size before assigning
   RulesJson. Return field errors for invalid requests and leave the old
   definition usable after failure.
5. Store the replacement array and save once. Do not delete/recreate the
   cohort, clear static membership tables, or eagerly recompute every flag.
6. Checkpoint: ask the existing cohort persons route for members before and
   after replacement. The same cohort ID now resolves to the pro audience.
7. Evaluate the targeting flag for one pro and one basic person. Confirm the
   new membership flows through the existing evaluator on the next request.
   If MID-17's request-local cache exists, ensure it does not cross requests.
8. Document last-successful-write-wins behavior: simultaneous editors may
   overwrite each other's definition. A future version check is separate work;
   no optimistic concurrency guarantee is claimed here.
9. Run focused tests plus cohort and flag regressions and add the replacement
   example to the API reference.

**Acceptance and verification:** A pro replacement changes both member results
and flag targeting while retaining the cohort ID. Invalid second rules cause
zero changes. Empty arrays, unknown operators, static cohorts, and foreign IDs
have the specified errors. No CohortPerson rows are inserted. Run
`dotnet test --filter "FullyQualifiedName~CohortRuleUpdateTests|FullyQualifiedName~CohortTests|FullyQualifiedName~FeatureFlagTests"`.

**Common mistake:** Deleting and recreating the cohort, which leaves existing
references pointing to the old ID even if the display name looks unchanged.

**Done when:** Definition replacement propagates through actual consumers and
invalid updates preserve the prior audience.

**Teach back:** Explain why a stable ID lets several features share a changing
definition. What would break if the ID changed on every edit?

<a id="mid-14"></a>
## MID-14 — Freeze a dynamic cohort into a static copy

**User story:** As an analyst, I want to save the people currently matching a
dynamic cohort as a static copy so I can compare a fixed audience later.

**Starting point and learning goal:** Dynamic cohorts evaluate rules on demand;
static cohorts store CohortPerson rows. Convert one evaluated result into a
new static cohort without changing the source. Reuse MID-12's evaluator if present.

**Open:** [CohortService](../src/Pulse.Infrastructure/Services/CohortService.cs),
[CohortEndpoints](../src/Pulse.Api/Endpoints/CohortEndpoints.cs),
[CohortPerson](../src/Pulse.Domain/Entities/CohortPerson.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[CohortTests](../tests/Pulse.Tests/Api/CohortTests.cs).

**Proposed contract:** `POST /api/projects/P/cohorts/C/snapshot` with
`{"name":"Launch audience"}` returns 201
`{cohort: CohortResponse,memberCount,evaluatedAt}` and the new cohort's Location.
Require a trimmed name of 1–200 characters and a dynamic source. Static source
returns 409. Snapshot at most 1,000 matching people; exceeding that returns
409 without creating anything. Empty membership creates a valid empty static
cohort. The source remains dynamic. This freezes membership IDs, not profiles
or the underlying events, and existing person deletion can remove members later.

**Implementation steps:**

1. Create `CohortSnapshotTests` with two matching people and one nonmatch.
   Plan an assertion that later property changes affect the source only.
2. Add the request/response and member-only endpoint. Start a service
   transaction before reading the source and evaluating its membership so
   all data reads and the inserted snapshot form one SQLite transaction.
3. Verify the source's project and dynamic type. Capture one evaluation time
   through TimeProvider and pass it to the shared dynamic evaluator.
4. Obtain the complete matched set, check the 1,000-member cap, and revalidate
   IDs against people in this project within the transaction. Do not silently
   discard unexpected missing or foreign IDs; return a conflict and no snapshot.
5. Build a new static Cohort with `RulesJson="[]"` and new CohortPerson rows
   for the unique IDs. Set the new cohort's creation timestamp from the captured
   evaluation time. Keep source metadata out of the existing schema.
6. Save once and commit before returning 201. On SQLite contention, roll back
   the complete unit; any bounded retry must re-evaluate in a fresh transaction.
7. Checkpoint: alter a person's properties after snapshot, then compare source
   and copy membership. Existing copied IDs should not follow rule changes.
8. Test over-cap, empty, cross-project, and injected-save-failure cases.
   Document that source linkage and historical profile values are not persisted.

**Acceptance and verification:** Two matches create one static cohort and two
membership rows. The source ID, type, and rules are unchanged. A later rule
change does not rewrite the copy. A 1,001-member result creates zero rows;
failure during persistence leaves no orphan membership rows. Run
`dotnet test --filter "FullyQualifiedName~CohortSnapshotTests|FullyQualifiedName~CohortTests"`.

**Common mistake:** Calling a sampled preview response the full membership
snapshot, or assuming static membership freezes each person's properties.

**Done when:** The copy's exact meaning and transactional persistence are
demonstrated with changing source data.

**Teach back:** Describe what is frozen and what can still change after the
snapshot. Use one person whose plan changes the next day.

<a id="mid-15"></a>
## MID-15 — Replace a static cohort's member set

**User story:** As an analyst, I want to replace a static audience with a supplied
list so I do not have to calculate individual add/remove requests myself.

**Starting point and learning goal:** The current API adds known people and
removes one member. Add an atomic set-replacement operation with stricter
validation, without changing those older endpoints' behavior.

**Open:** [CohortEndpoints](../src/Pulse.Api/Endpoints/CohortEndpoints.cs),
[cohort contracts](../src/Pulse.Api/Contracts/CohortContracts.cs),
[CohortPerson](../src/Pulse.Domain/Entities/CohortPerson.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[CohortTests](../tests/Pulse.Tests/Api/CohortTests.cs).

**Proposed contract:** `PUT /api/projects/P/cohorts/C/persons` accepts
`{"personIds":["GUID-A","GUID-B"]}` and returns
`{added,removed,unchanged,total}`. Null/missing list is 400; an empty list means
clear the audience. Allow at most 1,000 supplied IDs and deduplicate them.
Reject any unknown/foreign-project person with a generic 400 that does not
reveal where the ID exists. A dynamic cohort returns 409. Repeating the same
request returns zero added/removed with the same final membership.

**Implementation steps:**

1. Create `CohortMemberReplacementTests`: existing members A/B/C, requested
   B/C/D. Write expected counts: added 1, removed 1, unchanged 2, total 3.
2. Define replacement DTOs separately from the existing add request. State
   empty-list behavior in the contract so clearing is intentional and testable.
3. Add the member-only route. Check the raw list cap before deduplicating;
   a huge repeated-ID payload must not bypass the input bound.
4. Begin a transaction, load C by project and ID, and verify static type.
   Query all requested people in P and compare sets before deleting anything.
5. Load the current membership set and compute `toAdd`, `toRemove`, and
   `unchanged` using set operations. Keep these definitions in one service
   method so the counts describe the actual intended changes.
6. Delete only removed rows and add only new rows within the same transaction.
   If using ExecuteDeleteAsync, remember that it runs immediately; keep its
   rollback boundary with the subsequent additions and SaveChanges.
7. Commit before returning the counts. State concurrent replacement behavior
   as serialized successful writes/last committed set, with no merge of two
   independently submitted target lists. Handle contention as a whole unit.
8. Checkpoint: submit the B/C/D request twice and then an empty list. Inspect
   membership rows after each step and verify there are no duplicates.
9. Add invalid-person and injected-failure cases, run tests, and document how
   this strict replacement differs from the existing add-known-people route.

**Acceptance and verification:** The fixture counts are exact. Duplicates do
not inflate total; an empty list clears the set. One invalid ID preserves the
entire previous set. A failure after deletion rolls back deletions and additions.
Run `dotnet test --filter "FullyQualifiedName~CohortMemberReplacementTests|FullyQualifiedName~CohortTests"`.

**Common mistake:** Deleting the old set first and discovering an invalid new
person afterward, or reporting raw input length as unique membership count.

**Done when:** Repetition, clearing, mixed validity, and failure atomicity all
match the replacement contract.

**Teach back:** Explain idempotence using the B/C/D example. Which response
counts change on the second call, and which stored state does not?

## Feature flags and credentials

<a id="mid-16"></a>
## MID-16 — Explain a feature-flag decision

**User story:** As a developer, I want to see why a flag is on, off, or assigned
a variant for one identity so I can debug targeting without guessing.

**Starting point and learning goal:** The flag service evaluates active status,
targeting, rollout, and variants, but `/decide` returns only the final values.
Expose a member-only explanation generated by the same evaluation path.

**Open:** [FeatureFlagService](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs),
[FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlagHasher](../src/Pulse.Domain/FeatureFlagHasher.cs),
[FlagVariants](../src/Pulse.Domain/FlagVariants.cs), and
[FlagTargetingEdgeTests](../tests/Pulse.Tests/Api/FlagTargetingEdgeTests.cs).

**Proposed contract:** `POST /api/projects/P/feature-flags/KEY/explain` with
`{"distinctId":"visitor-7"}` returns `{key,value,personResolved,
stages:[{stage,outcome}],warnings:[]}`. Require trimmed identity length 1–400.
Stages are active, targeting, rollout, and variant; outcomes identify passed,
failed, selected, or skipped with a short stable reason code. Preserve existing
evaluation behavior, including invalid-targeting and invalid-variant fallbacks,
but report those as warnings. Do not return a person's properties or cohort
member lists. Missing/foreign key returns 404. The write-key `/decide` response
and authentication remain unchanged.

**Implementation steps:**

1. Create `FlagExplanationTests` covering inactive, targeting miss, zero/100%
   rollout, and a multivariate selection. Capture the existing `/decide` value
   for each as a compatibility expectation.
2. Define explanation DTOs and reason-code constants. Write a stage table:
   after an early failure, later stages are skipped rather than evaluated.
3. Refactor the private evaluator to produce a structured decision internally;
   let EvaluateAllAsync project only its final value. Do not maintain a second
   copy of the rollout/targeting algorithm for explanations.
4. Make current fallback behavior explicit: malformed stored targeting is
   currently treated as unrestricted; missing/invalid multivariate variants
   can fall back to boolean-on. Preserve outcomes and add warning codes.
5. Add the member-only endpoint, lookup by project and exact key, and resolve
   person context using the same identity lookup as normal evaluation.
6. Return only stage outcomes, boolean person resolution, warnings, and final
   value. For targeting failures, a rule index is sufficient; avoid dumping
   the actual property value that failed the rule.
7. Checkpoint: assert that explanation.value equals `/decide` for every fixture
   and that repeated calls preserve deterministic rollout/variant results.
8. Add seeded malformed-config tests plus the existing hash golden-value
   regression tests. Document that explanation reads current state, not an
   audit record of what an earlier SDK request saw.

**Acceptance and verification:** Inactive flags skip later gates; unknown
people follow current targeting semantics; warnings explain legacy fallbacks.
No explanation changes any flag or person row. Project write keys cannot call
this diagnostic endpoint. Run
`dotnet test --filter "FullyQualifiedName~FlagExplanationTests|FullyQualifiedName~FeatureFlagTests|FullyQualifiedName~FeatureFlagHasherTests"`.

**Common mistake:** Accidentally changing the decision algorithm while adding
diagnostics, then presenting disagreement with `/decide` as a logging problem.

**Done when:** Every explanation matches the actual decision and exposes only
the intended debugging information.

**Teach back:** Why should a diagnostic feature share execution logic with the
feature it explains? What kind of bug does duplicated logic invite?

<a id="mid-17"></a>
## MID-17 — Preview flags for a small audience

**User story:** As a developer, I want to evaluate selected flags for several
identities in one request so I can check a rollout against a small test audience.

**Starting point and learning goal:** EvaluateAllAsync reads flags and resolves
one identity per call. Add a bounded management preview that shares database
reads within a request. MID-16's structured evaluator is useful but not required.

**Open:** [FeatureFlagService](../src/Pulse.Infrastructure/Services/FeatureFlagService.cs),
[CohortService](../src/Pulse.Infrastructure/Services/CohortService.cs),
[FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[feature-flag contracts](../src/Pulse.Api/Contracts/FeatureFlagContracts.cs), and
[FeatureFlagTests](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Proposed contract:** `POST /api/projects/P/feature-flags/evaluate-batch` accepts
`{distinctIds:["alice","bob"],keys:["new_nav","checkout"]}` and returns
`{results:[{distinctId,featureFlags:{...}}]}` in identity input order. Require
1–50 unique trimmed identities of 1–400 characters and 1–20 unique exact keys
of 1–200 characters. Duplicates return 400. Unknown selected keys return a
generic 400 before evaluation; keys in another project are equally unknown.
Unknown identities remain valid and use existing no-person semantics.

**Implementation steps:**

1. Create `FlagBatchEvaluationTests` with two flags targeting the same cohort
   and three identities, one of which has no Person record.
2. Add batch DTOs and the member-only route. Validate both raw list sizes and
   every normalized value before doing expensive reads.
3. Add a service method loading just the selected project's flags once.
   Compare the found key set with the requested set; do not partially evaluate
   a request containing an unknown key.
4. Resolve all matching distinct-ID mappings in one scoped query, then load
   the needed persons/properties in a second query. Build lookup dictionaries
   so identity aliases can reuse the same person context.
5. Extract an evaluation-context parameter for the existing evaluator if
   needed. Cache each referenced cohort's member set for this request only;
   populate it sequentially with the existing cohort service.
6. Evaluate the bounded identity/flag combinations through the shared decision
   logic. Preserve output identity order and exact key casing. Never reuse
   results across projects or across later HTTP requests.
7. Checkpoint: compare every batch value with separate existing `/decide`
   responses over unchanged data. Verify the unknown identity creates no person.
8. Use targeted query instrumentation to verify flag/person/cohort data is not
   reloaded for every pair. Assert scaling properties rather than a brittle
   exact global SQL count that includes authentication and test setup.
9. Document that the endpoint is a live preview, not a historical snapshot,
   and test that changing a cohort is visible to the next request.

**Acceptance and verification:** Values match single-identity evaluation;
shared cohort membership is loaded once per request. Oversized, duplicate,
or partly invalid lists return 400 with no writes. Subsequent requests see new
state. Run `dotnet test --filter "FullyQualifiedName~FlagBatchEvaluationTests|FullyQualifiedName~FeatureFlagTests|FullyQualifiedName~FlagTargetingEdgeTests"`.

**Common mistake:** Adding a singleton cache of cohort IDs and returning stale
or cross-project decisions without a defined invalidation policy.

**Done when:** The batch is behaviorally equivalent to existing evaluation and
its bounded read reuse is supported by measurement.

**Teach back:** Explain why resolving one person once can help several aliases,
but hashing must still use each requested distinct ID.

<a id="mid-18"></a>
## MID-18 — Clone a flag as an inactive draft

**User story:** As a developer, I want to copy an existing flag into an inactive
draft so I can experiment with targeting without affecting the live flag.

**Starting point and learning goal:** Flag creation already validates key,
rollout, filters, and variants, and the database has a unique project/key index.
Reuse those checks while treating duplicate-key races as an expected conflict.

**Open:** [FeatureFlagEndpoints](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlag](../src/Pulse.Domain/Entities/FeatureFlag.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs),
[FeatureFlagHasher](../src/Pulse.Domain/FeatureFlagHasher.cs), and
[FeatureFlagTests](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Proposed contract:** `POST /api/projects/P/feature-flags/SOURCE/clone` with
`{"key":"checkout_experiment","name":"Checkout experiment"}` returns 201
`FeatureFlagResponse`. Require a 1–200-character key using the current allowed
ASCII letters/digits/hyphen/underscore pattern and a trimmed name of 1–200
characters. Copy type, rollout, filters, and variants; always set Active=false.
New ID and CreatedAt, same project. Existing target key returns 409. Invalid
stored source configuration returns 409 without creating a draft.

**Implementation steps:**

1. Create `FlagCloneTests` with an active multivariate source and explicit
   variants. Write expected copied fields and expected differing fields.
2. Add a clone request DTO and member-only route. Lookup the source using
   project and exact key; do not expose another project's same-named source.
3. Extract a narrowly shared validator from current create/update logic if
   necessary. Validate the requested key/name and the copied configuration.
   Keep source validation errors separate from caller input errors.
4. Check for a target-key collision for a friendly response, then create a new
   entity with an explicit inactive state. Do not copy the entire entity or
   accept an Active field from this request.
5. Save and handle the database's unique-key violation for the project/key
   index as 409. Inspect the actual provider error/constraint; do not convert
   every DbUpdateException, including I/O failures, into "key already exists".
6. Checkpoint: evaluate both flags. The source's result is unchanged, and the
   clone is false while inactive. Later activation is done through the existing
   update endpoint, not as part of cloning.
7. Explain in documentation that changing the key changes deterministic
   rollout/variant hashing; an active clone need not assign the same identities
   to the same outcome even if its percentages and filters match.
8. Test two competing clone requests for the same key using separate contexts.
   The persisted result must contain at most one target flag, with no changes
   to the source. Run hash regressions as well as the feature tests.

**Acceptance and verification:** The new flag is always inactive and has new
identity/timestamp; copied targeting and variant JSON is equivalent. Duplicate
keys produce 409, foreign sources 404, malformed stored sources 409. Run
`dotnet test --filter "FullyQualifiedName~FlagCloneTests|FullyQualifiedName~FeatureFlagTests|FullyQualifiedName~FeatureFlagHasherTests"`.

**Common mistake:** Assuming the pre-insert existence check prevents all
duplicates, or promising the clone has identical rollout assignments.

**Done when:** Draft creation, collision handling, and unchanged live behavior
are verified and the key-dependent hashing consequence is documented.

**Teach back:** Why do you need both a friendly duplicate check and a database
unique index? What can happen between the check and save?

<a id="mid-19"></a>
## MID-19 — Rotate a project's read key

**User story:** As a project member, I want to replace the project's read key
so I can retire a credential without interrupting event capture or user sessions.

**Starting point and learning goal:** Projects already have separate write
and read keys; read authorization compares the supplied key with the stored
ReadKey. Add an intentional management operation with a stale-request check.
The current membership model has no owner/admin role: any member may rotate it.

**Open:** [ProjectEndpoints](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs),
[ApiKeyGenerator](../src/Pulse.Domain/ApiKeyGenerator.cs),
[Project](../src/Pulse.Domain/Entities/Project.cs), and
[AuthzMatrixTests](../tests/Pulse.Tests/Api/AuthzMatrixTests.cs).

**Proposed contract:** `POST /api/projects/P/read-key/rotate` accepts
`{"expectedReadKey":"rk_live_CURRENT"}` under member authentication and returns
200 `{projectId,readKey}` containing the newly generated key. A missing/blank
expected key is 400; a stale or mismatched value is 409. Set `Cache-Control:
no-store` on successful key responses. The old key fails subsequent read-key
checks after commit; requests already authorized may complete. No grace period,
write-key rotation, user-token revocation, or new role system in this story.

**Implementation steps:**

1. Create `ProjectReadKeyRotationTests` and separate clients for the member,
   old read key, and write key. First prove each current credential's scope.
2. Add the request/response DTOs. Keep the expected credential in the body,
   never a query string, and do not add credential values to logs or errors.
3. Add the member-only route. Validate expected key shape/length against the
   actual generator format rather than accepting an unbounded string.
4. Generate a candidate with ApiKeyGenerator.NewReadKey. Use a conditional
   database update whose predicate includes project ID and expected current
   ReadKey. Return 409 when no row matches after authorization.
5. Return the new key only after the update succeeds. If using ExecuteUpdate,
   avoid returning stale data from an already tracked Project. Do not update
   ApiKey, memberships, or personal key records.
6. Checkpoint: call a read endpoint with the old key and expect rejection;
   call it with the returned new key and expect success. Capture with the
   unchanged write key and wait for the event to process successfully.
7. Test two rotations using the same expected key. At most one succeeds; the
   losing request must not overwrite the winner. A retry after a lost response
   can fetch current project details using member auth before deciding next steps.
8. Add cache-header and unchanged-field assertions. Document effects on clients,
   lack of grace period, and the distinction between committed rotation and
   cancellation of already-running requests.

**Acceptance and verification:** Rotation changes only ReadKey. Anonymous,
write-key-only, and read-key-only clients cannot rotate it. A stale expected key
returns 409 without a second change. JWT/personal-key access keeps working.
Run `dotnet test --filter "FullyQualifiedName~ProjectReadKeyRotationTests|FullyQualifiedName~AuthzMatrixTests|FullyQualifiedName~ProjectEndpointsTests"`.

**Common mistake:** Replacing both project keys because the response DTO carries
both, or doing a read-check followed by an unconditional update.

**Done when:** Credential behavior before and after rotation is demonstrated
through real endpoints and the stale-request test prevents lost updates.

**Teach back:** Explain why rotating a read key does not sign out a member's
JWT session. Trace the two authentication paths in ProjectAccessService.

## Ingestion and recovery features

<a id="mid-20"></a>
## MID-20 — Validate a capture request without enqueueing it

**User story:** As an SDK developer, I want to validate and inspect normalized
capture input so I can debug an integration without creating analytics data.

**Starting point and learning goal:** CaptureEndpoints.Unwrap validates a single
event or batch and normalizes it into IncomingEvent records. Extract this
parsing behavior so a management preview and actual capture cannot drift apart.

**Open:** [CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs),
[capture contracts](../src/Pulse.Api/Contracts/CaptureContracts.cs),
[CaptureService](../src/Pulse.Infrastructure/Services/CaptureService.cs),
[IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs),
and [CaptureEndpointsTests](../tests/Pulse.Tests/Api/CaptureEndpointsTests.cs).

**Proposed contract:** `POST /api/projects/P/capture/validate` takes the existing
single/batch event fields under member authentication. Reject a supplied
`api_key` field with 400; project P comes from the route. Success is 200
`{valid:true,eventCount,preview:[...],previewTruncated}` with at most three
normalized events. Invalid event input returns the existing indexed validation
problem shape. Use the current 1,000-event batch limit and a 1 MiB request-body
limit for this new endpoint. This validates capture admission only; it does
not guarantee later identity processing or database writes will succeed.

**Implementation steps:**

1. Create `CaptureValidationPreviewTests` with a single event, a batch, and
   whitespace around names/identities. Record current capture normalization:
   batch takes precedence and non-object properties become an empty object.
2. Extract Unwrap/Validate/ToIncoming into a small API-layer parser with no
   database dependencies. Keep CaptureRequest in API; do not make Domain
   depend on API contracts.
3. Make the existing capture handler call that parser after its current
   write-key authentication. Preserve queueing, rate limiting, status, field
   errors, and signal-after-save behavior. Add regression coverage for any
   explicit null batch items so malformed input receives a controlled 400.
4. Add the new route with RequireMemberAsync. Enforce its body size limit,
   reject embedded credentials, and invoke exactly the same parser.
5. Project the first three normalized events into a preview DTO. Keep an
   omitted timestamp null: normalization has not chosen the worker's eventual
   timestamp. State eventCount for the whole valid input, not just the preview.
6. Never call CaptureAsync, add queue entities, ring the signal, or update
   identity/registry tables. Set no-store on the preview response because it
   can contain supplied identities and property values.
7. Checkpoint: compare normalized content with what the actual capture path
   queues in an isolated service-level fixture where processing is controlled.
8. Assert all relevant table counts remain unchanged after successful and
   invalid previews. Run capture regressions and document the admission-only
   promise prominently beside the route example.

**Acceptance and verification:** A four-event batch reports four with three
preview entries and previewTruncated=true. Invalid item 2 has indexed errors
and no writes. Supplied credentials and oversized body fail; the original
capture endpoint still returns 202 for valid input. Run
`dotnet test --filter "FullyQualifiedName~CaptureValidationPreviewTests|FullyQualifiedName~CaptureEndpointsTests|FullyQualifiedName~IngestionPipelineTests"`.

**Common mistake:** Calling real capture and deleting generated rows afterward,
or claiming a parsed envelope proves the worker can process it successfully.

**Done when:** Both routes share normalization and the preview's zero-side-effect
promise is verified against storage.

**Teach back:** Walk through three separate milestones: valid request, durable
queue acceptance, and successfully processed event. Which one does this prove?

<a id="mid-21"></a>
## MID-21 — Explain whether a dead letter can be replayed

**User story:** As an operator, I want specific reasons a stored failure cannot
be replayed so I can understand it before attempting recovery.

**Starting point and learning goal:** IngestionOperationsService.CanReplay
returns only a boolean; ReplayAsync returns a generic invalid-payload outcome.
Produce a reusable structured validation result without changing replay policy.

**Open:** [IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
[IngestionEndpoints](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[DeadLetterEvent](../src/Pulse.Domain/Entities/DeadLetterEvent.cs),
[IngestionOperationsTests](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs),
and [ingestion runbook](../docs/runbooks/ingestion.md).

**Proposed contract:** `GET /api/projects/P/ingestion/dead-letters/L/replay-check`
returns 200 `{letterId,replayable,issues:[{code,field,message}]}` for an existing
letter. Codes cover invalid envelope JSON, missing envelope, missing event name,
missing distinct ID, missing properties JSON, invalid properties JSON, and
properties not being an object. Do not echo payload contents in issues. Missing
or foreign letter returns 404. A true result means the current stored envelope
passes replay admission; later processing can still fail.

**Implementation steps:**

1. Create `DeadLetterReplayCheckTests` using SeedLetterAsync as an example.
   Seed one payload for each current invalid case plus a valid IncomingEvent.
2. Extract a pure validation helper within Infrastructure, where IncomingEvent
   is already defined. Return a typed list of issue codes and fields, not an
   IResult or an exception message intended for the HTTP response.
3. When the outer JSON cannot be parsed, return that root issue and stop;
   dependent fields cannot be inspected. For a readable envelope, collect all
   independent missing-field issues before parsing supplied properties JSON.
4. Make existing ReplayAsync use the helper's validity flag. Preserve its
   transaction, 422 response, retained invalid letter, and signal-after-commit
   behavior. Do not weaken or silently expand accepted payload types.
5. Add the member-only check route, query by project and letter ID with
   AsNoTracking, run the helper, and map issues into the new response.
6. Checkpoint: run check then replay for valid/invalid fixtures. Their admission
   decisions should agree when the stored letter has not changed.
7. Test that the check leaves letter, queue, events, and process counters
   unchanged. Do not promise that checking reserves the letter: a concurrent
   operator may replay it before the caller's next request.
8. Add an example diagnostic sequence to the runbook, distinguishing capture
   request JSON from the serialized IncomingEvent envelope stored in dead letters.
   Keep the original replay tests in the regression command.

**Acceptance and verification:** Invalid nested properties reports a different
code from invalid outer JSON. Multiple missing fields are returned together.
The valid fixture returns replayable=true and an empty issue list. Rechecking
a consumed letter returns 404. Run
`dotnet test --filter "FullyQualifiedName~DeadLetterReplayCheckTests|FullyQualifiedName~IngestionOperationsTests"`.

**Common mistake:** Validating stored envelopes as public CaptureRequest JSON,
or presenting a replay check as a reservation or processing guarantee.

**Done when:** Check and action share one admission rule and diagnostics are
specific without leaking raw payloads into error text.

**Teach back:** Explain why a successful preflight can be followed by 404 on
replay without either endpoint being broken.

<a id="mid-22"></a>
## MID-22 — Replay a selected batch of dead letters

**User story:** As an operator, I want to replay several selected failures in
one request and see each outcome so I can recover a small incident efficiently.

**Starting point and learning goal:** Single-letter replay already atomically
moves a valid letter to the queue. Compose that operation with explicit partial
success semantics instead of silently changing it into one large transaction.

**Open:** [IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
[IngestionEndpoints](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[capture contracts](../src/Pulse.Api/Contracts/CaptureContracts.cs),
[IngestionOperationsTests](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs),
and [IngestionTransactionTests](../tests/Pulse.Tests/Infrastructure/IngestionTransactionTests.cs).

**Proposed contract:** `POST /api/projects/P/ingestion/dead-letters/replay-batch`
accepts `{"letterIds":["GUID-A","GUID-B"]}`. Require 1–20 unique nonempty
GUIDs. Success is 200 `{results:[{letterId,outcome}],queued,invalid,notFound}`,
preserving request order; outcomes are `queued`, `invalidPayload`, and
`notFound`. Foreign-project IDs are indistinguishable from missing IDs.
Each item commits independently. The response means accepted into the queue,
not successfully processed. MID-21 diagnostics are optional follow-up reads.

**Implementation steps:**

1. Create `DeadLetterBatchReplayTests` with one valid letter, one invalid
   envelope, one nonexistent ID, and one letter belonging to another project.
2. Define batch DTOs and add the member-only route. Validate the complete list,
   including duplicates and size, before starting any replay transaction.
3. Add a small orchestration method that calls existing ReplayAsync sequentially.
   Do not wrap the loop in an outer transaction, since ReplayAsync already
   owns its per-letter transaction and committed progress is part of the contract.
4. Map the three expected domain outcomes into result entries. Keep aggregate
   counts derived from those entries so summary and details cannot disagree.
5. Pass cancellation through. If an unexpected database failure occurs, stop
   and let the request fail; do not continue using a context with uncertain
   tracked state or report an invented per-item success.
6. Document the interrupted-request case: earlier items may already have
   committed even if no complete response reaches the client. On resubmission,
   consumed letters return notFound. This is not an exactly-once event contract.
7. Checkpoint: submit the mixed batch, wait for ingestion to drain, and assert
   only the valid letter's event was processed. The invalid and foreign letters
   must still exist.
8. Add duplicate-input rejection and a controlled failure on a later item
   using service-level fault injection. Prove earlier commits survive and
   later unattempted letters remain available. Update the runbook.

**Acceptance and verification:** The mixed fixture returns one queued, one
invalid, and two notFound results in input order. A duplicate-ID request performs
zero replays. Retrying a consumed ID never creates a second queue row from that
same letter. Failure/cancellation does not claim to roll back earlier items.
Run `dotnet test --filter "FullyQualifiedName~DeadLetterBatchReplayTests|FullyQualifiedName~IngestionOperationsTests|FullyQualifiedName~IngestionTransactionTests"`.

**Common mistake:** Returning a single 202 for a mixed batch with no per-item
outcomes, or promising all-or-nothing behavior around independently committed work.

**Done when:** Mixed results, retry behavior, and interrupted-request semantics
are both tested and described to operators.

**Teach back:** Compare this batch with MID-03's layout batch. Why are their
transaction boundaries different even though both accept lists?

## Export features

<a id="mid-23"></a>
## MID-23 — Retry a failed export as a new job

**User story:** As an analyst, I want to retry a failed export after its cause
is fixed so I can obtain the data while preserving the original failure record.

**Starting point and learning goal:** Export jobs move through Pending, Running,
Completed, and Failed. Create a new Pending job from a failed one's inputs,
using the existing worker rather than executing export work inside the endpoint.

**Open:** [ExportEndpoints](../src/Pulse.Api/Endpoints/ExportEndpoints.cs),
[ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs),
[ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[export contracts](../src/Pulse.Api/Contracts/ExportContracts.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract:** `POST /api/projects/P/exports/J/retry` with no body returns
202 `{sourceJobId,job:ExportJobResponse}` and a Location for the new job.
Only Failed source jobs are eligible; other states return 409. Missing/foreign
source returns 404. Copy Type, Format, and ParamsJson; clear output, error,
completion timestamp, and row count through new-entity defaults. Source remains
unchanged. Repeated retry requests intentionally create distinct jobs; no
idempotency key or persisted parent relationship is added in this first version.

**Implementation steps:**

1. Create `ExportRetryTests` with a failed job whose ParamsJson describes a
   valid event export, and a separate failed insight job whose insight is missing.
   Seed failures directly in the isolated database to keep setup deterministic.
2. Add the response wrapper and member-only route. Look up the source using
   project and ID, then verify Failed status before constructing anything.
3. Add a small job-creation service or shared helper so creation/retry both
   express save-first, signal-second ordering. Keep request validation in the
   existing create path compatible.
4. Copy only the input fields into a new ExportJob with a new ID and captured
   creation time. Preserve the original ParamsJson exactly; do not silently
   replace bad parameters or retarget a missing insight.
5. Save the new row successfully before ringing ExportSignal. Return the new
   job metadata, with status potentially already advanced by the worker by the
   next read. Do not assert that a subsequent GET must still say pending.
6. Checkpoint: poll the new job through existing status/download routes with
   a bounded condition-based wait. The repaired event export should complete.
   The unresolved missing-insight retry can legitimately fail again.
7. Test that the original failed row retains its error and completion time.
   A failure to save the new job must not modify the source or signal success.
8. Document that retry reruns against current data and current saved insight
   config, not a historical database snapshot, and that repeated clicks create
   multiple jobs. Add an example of following the returned Location.

**Acceptance and verification:** The new ID differs and input fields match.
Output/error fields are not copied; nonfailed sources return 409 with no new
job. The original failed source still returns 409 from download even after its
retry completes; only the new completed job becomes downloadable. Run
`dotnet test --filter "FullyQualifiedName~ExportRetryTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Resetting the original row to Pending and erasing the
failure evidence, or ringing the worker before the new row is durable.

**Done when:** Retry uses the existing durable workflow and preserves both
the old failure and the new attempt's independent lifecycle.

**Teach back:** Explain why retrying an export tomorrow can produce different
data from the failed attempt today, even with identical parameters.

<a id="mid-24"></a>
## MID-24 — Browse export history

**User story:** As an analyst, I want to page through recent export jobs and
filter their status so I can find a completed download or investigate a failure.

**Starting point and learning goal:** Exports currently provide create, get,
and download routes, but no history list. Add a metadata-only cursor query;
ResultContent can be large and should not be loaded for a history screen.

**Open:** [ExportEndpoints](../src/Pulse.Api/Endpoints/ExportEndpoints.cs),
[ExportService](../src/Pulse.Infrastructure/Services/ExportService.cs),
[ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[export contracts](../src/Pulse.Api/Contracts/ExportContracts.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract:** `GET /api/projects/P/exports?status=completed&type=events&limit=25&cursor=...`
returns `{jobs:[ExportJobResponse],nextCursor}` ordered by CreatedAt descending,
then ID descending. Status optionally accepts pending/running/completed/failed;
type optionally accepts events/persons/insight. Trim and lowercase names, treat
blank filters as omitted, reject unsupported values with 400. Limit defaults
to 25 and must be 1–100. No ParamsJson or ResultContent in the list.

**Implementation steps:**

1. Create `ExportHistoryTests` with several terminal jobs, one pending/running
   fixture in a worker-controlled setup, and matching jobs in another project.
2. Define the page DTO and member-only GET route. Parse filters through explicit
   named allowlists; numeric strings are not status names.
3. Add a history query with project, status, and type predicates before cursor
   filtering and pagination. Keep the sort and continuation directions aligned.
4. Use a small versioned history cursor containing purpose, project, normalized
   filter values, and the last returned CreatedAt/ID. Reject context mismatch,
   bad encoding, excessive length, and out-of-range timestamps with 400.
   Reuse a robust codec abstraction if MID-10 introduced one, not its purpose.
5. Select only metadata fields in SQL and take limit+1. Avoid loading ExportJob
   entities then mapping, which would also retrieve their potentially large
   inline downloads. Return a cursor only when an extra matching row exists.
6. Checkpoint: traverse tied CreatedAt values with page size one. Use explicit
   IDs and the same SQLite ordering for both sorting and comparisons.
7. Verify metadata-only SQL with a focused query inspection or interceptor;
   seed a large ResultContent to make accidental entity loading easy to spot.
8. Document live-list behavior: newer jobs appear on a restarted first page;
   status changes can move jobs into/out of a filtered list between pages.
   A cursor does not freeze membership. Run focused and export regressions.

**Acceptance and verification:** Filtering precedes paging, terminal history
is complete under ties, and cross-project jobs never appear. Unsupported
filters and mismatched cursors are 400. No content/parameters field is returned
or selected for the history query. Run
`dotnet test --filter "FullyQualifiedName~ExportHistoryTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Projecting after materialization and accidentally reading
every downloadable document just to display job status.

**Done when:** Pagination, filter semantics, and metadata-only database access
are verified, with live status changes described honestly.

**Teach back:** Explain the difference between selecting five DTO fields in
SQL and returning five DTO fields after loading a complete entity.

<a id="mid-25"></a>
## MID-25 — Delete a finished export and its download

**User story:** As a project member, I want to remove an old completed or
failed export so I can clean up history and stop serving its stored document.

**Starting point and learning goal:** ExportJob stores metadata and inline
ResultContent in one row. Add deletion conditioned on a terminal state; do not
race the worker by removing Pending or Running jobs.

**Open:** [ExportEndpoints](../src/Pulse.Api/Endpoints/ExportEndpoints.cs),
[ExportJobProcessor](../src/Pulse.Infrastructure/Services/ExportJobProcessor.cs),
[ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract:** `DELETE /api/projects/P/exports/J` returns 204 after
deleting a Completed or Failed job. Pending/Running jobs return 409; missing
or foreign jobs return 404. A second delete returns 404. Subsequent status and
download lookups return 404. A download that already loaded the document may
finish; deletion does not revoke bytes already sent. No event/person/insight
records are removed and no automatic retention scheduler is introduced.

**Implementation steps:**

1. Create `ExportDeletionTests` covering every existing ExportJobStatus.
   For pending/running cases, use controlled service-level fixtures or a
   feature-specific test host that disables only ExportWorker; otherwise
   the real worker can change the status before your assertion.
2. Add the member-only DELETE route and a small typed deletion outcome.
   Keep the new logic beside existing export job operations.
3. Perform a conditional delete with project ID, job ID, and terminal-state
   predicates in the actual database statement. Do not load a job, check its
   status, and then delete solely by ID in a later unguarded statement.
4. If one row is deleted, return 204. If zero rows are deleted, perform a
   project-scoped metadata lookup to distinguish missing from currently
   nonterminal and return 404 or 409. A concurrent disappearance can be 404.
5. Avoid materializing ResultContent for the delete decision. Deleting the
   job row removes its inline document; it does not require another blob or
   filesystem operation in this repository.
6. Checkpoint: download a completed fixture, delete it, then call status and
   download again. Both should now be 404 while captured event counts remain
   unchanged.
7. Add an interleaving test that changes a job from pending to running between
   calls. Prove neither state is deleted. If a job completes before the actual
   conditional delete, deletion is valid under the terminal-state contract.
8. Document no secure-erasure or immediate SQLite-file-shrink promise: the
   feature removes application access to the row. Update history examples if
   MID-24 is already implemented and run export regressions.

**Acceptance and verification:** Completed/Failed jobs are removed; Pending/
Running survive with 409. Foreign jobs survive with 404 to the caller. No
source data or other jobs change. Post-delete metadata/checksum routes, if
implemented, must use the same scoped existence behavior. Run
`dotnet test --filter "FullyQualifiedName~ExportDeletionTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Treating deletion as job cancellation and removing a row
the worker is still actively updating.

**Done when:** The database mutation itself enforces the state rule and the
post-deletion API behavior is demonstrated.

**Teach back:** Why should status be in the DELETE predicate even if you
already checked it earlier in the handler?

<a id="mid-26"></a>
## MID-26 — Verify a completed export's download bytes

**User story:** As an analyst downloading an export, I want its byte length
and SHA-256 checksum so I can verify that my saved file matches the server's
completed document.

**Starting point and learning goal:** Completed content is a .NET string
returned by the download endpoint. A checksum needs a precise byte encoding,
not a count of characters or a reserialized approximation of JSON.

**Open:** [ExportEndpoints](../src/Pulse.Api/Endpoints/ExportEndpoints.cs),
[ExportJob](../src/Pulse.Domain/Entities/ExportJob.cs),
[ExportService](../src/Pulse.Infrastructure/Services/ExportService.cs),
[Csv](../src/Pulse.Domain/Csv.cs), and
[ExportTests](../tests/Pulse.Tests/Api/ExportTests.cs).

**Proposed contract:** `GET /api/projects/P/exports/J/integrity` returns
`{jobId,contentType,byteLength,sha256,encoding:"utf-8"}` for Completed jobs.
SHA-256 is lowercase hex over the exact uncompressed UTF-8 document bytes,
without a BOM. Noncompleted jobs return 409; missing/foreign jobs return 404.
A completed job with null stored content is an inconsistent record and returns
409 rather than hashing an invented empty document. A legitimately empty
stored string remains valid. Compute on demand; no new schema fields or ETag
semantics in this story.

**Implementation steps:**

1. Create `ExportIntegrityTests` with ASCII CSV, JSON containing non-ASCII
   characters, an empty string, and content containing explicit CRLF line ends.
   Write expected byte arrays from those exact strings in the test.
2. Add the integrity DTO and member-only route. Scope the job lookup and
   apply the same completed-state gate as download, plus the null-content check.
3. Extract a narrow document-byte helper using UTF-8 without a BOM. Preserve
   stored line endings, whitespace, property order, and CSV quoting exactly.
   Never parse and reserialize the document before hashing it.
4. Compute SHA-256 over those bytes using the platform library and return
   their LongLength/length as byteLength with the content type actually served.
5. Route the existing download through the same byte conversion helper, using
   a byte response with the existing media type and explicit UTF-8 text charset
   where appropriate. Keep document contents and success status compatible.
6. Checkpoint: request download through HttpClient, read its content bytes,
   hash them independently in the test, and compare with integrity metadata.
   Verify non-ASCII byte length differs from .NET string length as expected.
7. Test unfinished jobs, missing records, null versus empty content, and
   cross-project access. Keep compression/transport framing outside the digest
   definition; clients verify the decoded document bytes they save.
8. Document O(document size) encoding/hash work and allocation. Current inline
   export storage already limits the architecture; this feature does not add
   streaming or prove that very large documents use little memory.

**Acceptance and verification:** JSON and CSV download hashes match metadata
byte-for-byte, including Unicode and line endings. Empty-string SHA is valid,
null content is a conflict, and nonmembers learn nothing about job contents.
Run `dotnet test --filter "FullyQualifiedName~ExportIntegrityTests|FullyQualifiedName~ExportTests"`.

**Common mistake:** Hashing character counts or normalized JSON while the
download endpoint emits different bytes.

**Done when:** The integrity response and actual HTTP document agree for
several encodings/content fixtures without stored-result changes.

**Teach back:** Explain why a checksum can verify matching bytes but cannot,
by itself, prove that an export contains all desired events.

## Discovery and project overview features

<a id="mid-27"></a>
## MID-27 — See event-name usage over a date range

**User story:** As an analyst, I want event counts and first/last occurrence
times by event name so I can discover which instrumentation is actually active.

**Starting point and learning goal:** Event definitions record observed names;
they do not store historical per-range usage counts. Build this feature from
processed Events and explain how registry metadata differs from analytics data.

**Open:** [DataManagementEndpoints](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[EventDefinition](../src/Pulse.Domain/Entities/EventDefinition.cs),
[AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs),
[QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs), and
[DataManagementTests](../tests/Pulse.Tests/Api/DataManagementTests.cs).

**Proposed contract:** `GET /api/projects/P/event-usage?from=...&to=...&limit=20`
returns `{from,to,totalEvents,eventNameCount,items:[{event,count,firstAt,lastAt}]}`.
Require a nonempty half-open range `[from,to)` of at most 90 days. Limit defaults
to 20 and must be 1–100. Order by count descending, then ordinal event name.
Names with zero events in the range are omitted even if a registry definition
exists. Counts include processed system events such as `$identify` if stored;
they exclude queued and dead-letter envelopes. No property filter in v1.

**Implementation steps:**

1. Create `EventUsageTests` with signup/purchase events, a retained definition
   with no current events, and similarly named events in project B.
2. Add request binding and response DTOs, using long event counts. Place the
   member-only route near registry routes but use a distinct path so a caller
   cannot mistake usage results for the definition list.
3. Add a focused query method with project and time filters in SQL. Group by
   event name, compute count/min/max, and order the groups before taking limit.
4. Calculate totalEvents and eventNameCount over the full filtered set, not
   only the returned top names. Keep all event payload JSON out of projections.
5. Verify the chosen ordinal tie order against SQLite. If using an explicit
   database collation, keep it consistent with API documentation and test
   case-sensitive names such as `Signup` and `signup` separately.
6. Checkpoint: use the existing person-deletion route to remove one fixture
   person and their events, then show that surviving registry names do not
   imply nonzero usage. Use only the test project's isolated data and account
   for that person's other removed events in your expected totals.
7. Add boundary, no-data, tied-count, top-limit, and access tests. Inspect the
   generated SQL to confirm aggregation occurs before materialization.
8. Document that counts from multiple SQL reads are a live view during
   ingestion; exact cross-field snapshot consistency is not promised in v1.
   Add a worked example separating registered names from active names.

**Acceptance and verification:** Three signup and one purchase event give
totalEvents=4 and eventNameCount=2 even with limit=1. Zero-use registry names
are absent. Boundary events at `to` are excluded; names differing by case remain
separate. Queries do not load property JSON. Run
`dotnet test --filter "FullyQualifiedName~EventUsageTests|FullyQualifiedName~DataManagementTests"`.

**Common mistake:** Counting EventDefinition rows as event occurrences or
computing summary totals from an already truncated top-N list.

**Done when:** Registry-versus-event semantics and full-set-versus-page counts
are demonstrated with a small exact fixture.

**Teach back:** A registry lists 20 names, but only three appear this week.
Explain why both numbers can be correct and answer different questions.

<a id="mid-28"></a>
## MID-28 — Explore the string values of an event property

**User story:** As an analyst, I want to see common string values of a property,
such as `plan` or `browser`, so I can choose sensible filters for an insight.

**Starting point and learning goal:** Existing trend breakdowns collapse
values into display labels. Build a bounded discovery query with explicit
missing/null/non-string counts so labels do not hide data-type differences.

**Open:** [QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs),
[PropertyFilters](../src/Pulse.Domain/PropertyFilters.cs),
[DataManagementEndpoints](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[AnalyticsEvent](../src/Pulse.Domain/Entities/AnalyticsEvent.cs), and
[FilterBreakdownTests](../tests/Pulse.Tests/Api/FilterBreakdownTests.cs).

**Proposed contract:** `GET /api/projects/P/property-values?event=signup&property=plan&from=...&to=...&limit=10`
returns `{event,property,totalEvents,values:[{value,count}],missingCount,
nullCount,nonStringCount,otherStringCount}`. Require literal top-level event/
property names of 1–200 characters, a nonempty half-open range of at most
31 days, and limit 1–25. Count strings exactly and case-sensitively, including
empty strings. Null is distinct from missing; numbers/booleans/arrays/objects
contribute to nonStringCount. Do not flatten or parse dotted property paths.
Refuse more than 10,000 matching rows or 8 MiB of scanned UTF-8 property JSON
with 422 and a suggestion to narrow the range. Return no partial statistics.

**Implementation steps:**

1. Create `PropertyValueDiscoveryTests` with `"pro"`, `"Pro"`, empty string,
   explicit null, missing property, number 1, and a string literally `"(other)"`.
2. Add DTOs and the member-only route. Validate names/range/limit before work.
   State literal top-level lookup rather than reusing a helper that stringifies
   other JSON types into potentially identical labels.
3. Query only this project's matching event name and half-open time slice.
   Select property JSON only, order deterministically, and scan at most 10,001
   rows so excess volume is detected instead of silently sampled.
4. Iterate rows sequentially, tracking cumulative UTF-8 byte size. Stop with
   422 at either cap. A row has to be read to inspect its size; do not claim
   the cap prevents allocation of a single oversized stored property string.
5. Parse each JSON object and use TryGetProperty for the exact key. Maintain
   separate counters and an ordinal string-frequency map. Malformed stored
   JSON returns a controlled 409 without exposing the payload or partial totals.
6. Sort string counts descending then ordinal value. Return the top limit,
   and sum omitted string counts into otherStringCount, a separate numeric
   field rather than a synthetic value that can collide with real strings.
7. Checkpoint: verify totalEvents equals returned value counts plus all four
   remainder/category counters. A string `"1"` must not merge with number 1.
8. Test both scan caps, category distinctions, and foreign-project isolation.
   Document that this feature currently explores only string values and does
   not infer a property's authoritative schema from samples.

**Acceptance and verification:** The fixture's categories reconcile exactly;
literal sentinel-looking strings remain ordinary values. Over-cap requests
return 422 without partial statistics, and a narrower range succeeds. Run
`dotnet test --filter "FullyQualifiedName~PropertyValueDiscoveryTests|FullyQualifiedName~FilterBreakdownTests"`.

**Common mistake:** Mapping missing, null, empty string, and the text `"null"`
to one display bucket and then reporting misleading filter suggestions.

**Done when:** Typed categories, scan bounds, and exact top-value counts are
verified independently of the existing trend-breakdown implementation.

**Teach back:** Explain why a small limit on returned values does not bound
how many events must be inspected to identify the most common values.

<a id="mid-29"></a>
## MID-29 — Diagnose a project's ingestion health

**User story:** As an operator, I want a project-specific ingestion status
with reasons so I can tell whether to investigate backlog or failed events.

**Starting point and learning goal:** The existing project metrics service
already reports pending count, oldest pending age, and dead-letter count.
Translate those facts into a documented policy without confusing a threshold
warning with proof that the worker is alive or dead.

**Open:** [IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
[IngestionEndpoints](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[Program](../src/Pulse.Api/Program.cs),
[IngestionOperationsTests](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs),
and [ingestion runbook](../docs/runbooks/ingestion.md).

**Proposed contract:** `GET /api/projects/P/ingestion/status` returns
`{status,observedAt,metrics,thresholds,reasons}` with status `ok` or `attention`.
Default thresholds are 60 seconds oldest pending age and 1,000 pending rows.
Optional `maxPendingAgeSeconds` accepts 1–3,600 and `maxPending` accepts
1–100,000. Return attention when any dead letter exists, pending is at least
maxPending, or a nonnull age is at least maxPendingAgeSeconds. Reason codes
are `dead_letters_present`, `queue_depth_high`, and `oldest_pending_too_old`
in that fixed order. Return 200 for both valid operational statuses; existing
global `/health` semantics do not change.

**Implementation steps:**

1. Create `ProjectIngestionStatusTests` and a pure policy test class. Write a
   truth table for empty, just-below, equal-to, and above each threshold.
2. Define response/threshold records. Extract a small pure classifier that
   accepts ProjectIngestionMetrics and validated thresholds, and returns state
   plus stable reason codes. No database calls belong in the classifier.
3. Add the member-only route and query validation. Read project metrics using
   the existing service; do not use global process counters as project evidence.
4. Capture one clock value for observedAt and age calculation. If necessary,
   add a service overload accepting that timestamp while retaining the existing
   metrics endpoint's response and default behavior.
5. Apply the classifier and echo effective thresholds so a caller can explain
   why two requests with different limits produce different statuses.
6. Checkpoint: feed the pure classifier pending=2, age=60, deadLetters=0 with
   defaults. Expect only oldest_pending_too_old. Empty queue age remains null,
   not a manufactured zero timestamp or an infinite age.
7. Use a controlled worker-free service fixture to seed queue/dead-letter state
   without the real worker draining it during assertions. Keep an HTTP test
   for authentication and response shape against the normal host.
8. Document limitations: status is a current observation, not an alert history,
   worker heartbeat, or liveness probe. Add runbook actions linked to each
   reason: inspect failures, inspect backlog, or inspect the oldest work.

**Acceptance and verification:** Equality activates thresholds, all applicable
reasons appear in fixed order, null age does not trigger age warnings, and
future-enqueued timestamps retain the existing nonnegative age behavior.
Invalid overrides return 400; another project's queue does not affect status.
Run `dotnet test --filter "FullyQualifiedName~ProjectIngestionStatusTests|FullyQualifiedName~IngestionStatusPolicyTests|FullyQualifiedName~IngestionOperationsTests"`.

**Common mistake:** Returning "healthy worker" because the queue is empty,
or treating a lifetime global processed counter as a project-specific rate.

**Done when:** Classification is deterministic and every displayed reason has
a precise input condition and useful runbook action.

**Teach back:** Could a stopped worker and an empty queue produce status ok?
Explain why that follows from this feature's actual promise.

<a id="mid-30"></a>
## MID-30 — Open a project overview with useful counts

**User story:** As a new project member, I want one overview of the project's
data and configured features so I know where to start exploring the codebase
and the product's current state.

**Starting point and learning goal:** Project detail exposes project metadata,
while feature counts live across many tables. Compose an intentionally small
overview that teaches what each table represents without returning credentials
or loading entire feature lists.

**Open:** [ProjectEndpoints](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs),
[IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs),
[ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs),
[ProjectEndpointsTests](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs), and
[feature tour](10-feature-tour.md).

**Proposed contract:** `GET /api/projects/P/overview` returns
`{project:{id,name,createdAt},observedAt,counts:{persons,events,insights,
dashboards,cohorts,featureFlags,activeFeatureFlags,completedExports},ingestion}`.
`ingestion` uses the existing project metrics shape. Counts describe stored
rows for P at read time: persons excludes alias mappings, events excludes queued
work, activeFeatureFlags means Active=true rather than "on for every person",
and completedExports excludes running/failed jobs. All counters are zero on
an empty project. Never include ApiKey, ReadKey, personal tokens, event payloads,
or inline export documents. No new admin role or cross-project overview.

**Implementation steps:**

1. Create `ProjectOverviewTests`. Draw a fixture with two people, three alias
   mappings, four events, one dashboard with two tiles, two flags of which one
   is active, and exports in two different states. Calculate each expected count.
2. Add dedicated overview DTOs. Do not reuse ProjectResponse because that
   response includes credentials. Link each count name to a table in a short
   comment or documentation table rather than exposing schema names to users.
3. Add a scoped ProjectOverviewService and register it. The endpoint checks
   membership and resolves project metadata with only safe fields selected.
4. Query scoped counts using CountAsync/LongCountAsync or suitable grouped
   projections. Use long values for potentially large event totals. Do not
   call paginated list endpoints to count their returned items.
5. Reuse GetMetricsAsync for ingestion. Run operations sequentially on the
   scoped context and capture observedAt through TimeProvider. If MID-29 added
   an explicit clock overload, use it consistently here.
6. Checkpoint: verify dashboard count is one, not two tile rows; person count
   is two, not three aliases; active flag count is independent of rollout
   percentages. Match event count to processed rows after the queue drains.
7. Document live-read consistency: the endpoint performs several reads and
   ongoing ingestion can change counts between them. observedAt labels the
   observation, not a transactionally frozen snapshot of every table.
8. Inspect SQL/projections for accidental payload/credential selection and
   unbounded entity loads. Add field-absence assertions and foreign-project
   fixtures, including a second project owned by the same user.
9. Update the feature tour with a walkthrough from overview field to endpoint,
   service, entity, and test. Let this be a capstone explanation of how the
   codebase fits together rather than another generic architecture description.

**Acceptance and verification:** All fixture counts match their definitions,
the empty project has zeros/null queue age, and no credential or payload fields
appear anywhere in serialized output. Another project's large dataset does not
change P's results. Run
`dotnet test --filter "FullyQualifiedName~ProjectOverviewTests|FullyQualifiedName~ProjectEndpointsTests|FullyQualifiedName~IngestionOperationsTests"`.

**Common mistake:** Counting a paginated response or joining unrelated
one-to-many tables and accidentally multiplying rows before aggregation.

**Done when:** Every overview value has a precise definition, a project-scoped
query, a fixture assertion, and a learner-friendly path back to its source.

**Teach back:** Explain one overview response twice: first to a product user,
then to an engineer who needs to find the code that produced each field.

## Reuse these explanations as you progress

The patterns intentionally repeat across different features. Repetition should
help you predict the next step, not make you memorize file contents.

| Pattern | Plain explanation | Concrete codebase practice |
| --- | --- | --- |
| Authorization and scoping | Check who may ask, then check which records belong here. | Membership guard plus ProjectId predicate |
| Validate before mutation | Understand the complete request before changing stored state. | MID-03 layouts and MID-15 member replacement |
| Atomic work | Either the promised group of writes exists, or none of it does. | Dashboard copy and cohort snapshot |
| Partial success | Some independent items can finish even when a later item fails. | MID-22 replay batch |
| Preview | Ask what would happen without persisting a new record. | Insight, cohort, and capture previews |
| Shared logic | One decision should not have two competing implementations. | Flag explanation and normal decide |
| Cursor pagination | Continue after a complete ordering position. | Person timeline and export history |
| Bounded work | Limit inputs and inspected data, not merely response size. | Property values and batch flag preview |
| Snapshot versus live view | State exactly which values are fixed and which can change. | Static cohort copy versus overview counts |
| Durable background work | Commit the job before waking the processor. | Export retry and dead-letter replay |

For every completed story, fill in this small learning record:

```text
Story ID and user-visible result:
Request -> access guard -> service -> tables -> response:
One fixture I calculated by hand:
One edge case that changed my implementation:
One tradeoff I chose and why:
Focused command, discovered test count, and actual result:
Required broader checks and actual results or concrete blockers:
What I can now explain without looking at the source:
```

Before requesting review, verify the new behavior through its public interface,
inspect the final diff, and explain the failure path as clearly as the success
path. If a runtime check is blocked, report it as unverified with the exact
reason. Writing a plan, adding a test file, or seeing a successful build alone
does not establish that a feature works.
