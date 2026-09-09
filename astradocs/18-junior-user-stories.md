# 25 junior user stories with follow-along implementation plans

**Status: proposed work only. None of these stories is implemented by this
document.** Examples below describe the behavior you will add and verify.
Existing features used as starting points are identified separately.

The junior stories have now been implemented as part of the requested full
bootcamp build. This document preserves their original learning plans. Use the
[implementation ledger](bootcamp/stories.json) and [journal](bootcamp/journal.md)
for evidence and remaining integration gates.

Choose one story. Read it once, write down the expected result, and follow
the numbered steps one at a time. You can stop between steps. Each story is
a small contribution; completing all 25 is not a prerequisite for contributing.

## Pick a story

| ID | User-visible improvement | Main practice |
| --- | --- | --- |
| [US-01](#us-01) | Choose the demo's event name | Parameters and consistent values |
| [US-02](#us-02) | Choose how many demo events to send | Loops and expected counts |
| [US-03](#us-03) | Find useful project routes after the demo | Useful output without credentials |
| [US-04](#us-04) | Restart the visual lesson | Small UI state changes |
| [US-05](#us-05) | Hide the quiz until ready | Accessible show/hide controls |
| [US-06](#us-06) | Look up three terms inside the lesson | Semantic HTML and clear explanations |
| [US-07](#us-07) | Get a clear error for an overlong project name | Input validation |
| [US-08](#us-08) | Rename a project | One small update endpoint |
| [US-09](#us-09) | Search project names | Filtering an existing query |
| [US-10](#us-10) | List newest projects first | Validated sort options |
| [US-11](#us-11) | Find project members by email | Filtering a joined query |
| [US-12](#us-12) | Filter people by an exact distinct ID | Related-row lookup without duplicates |
| [US-13](#us-13) | Filter people by creation date | Nullable dates and interval boundaries |
| [US-14](#us-14) | See the total number of people | One count endpoint |
| [US-15](#us-15) | Get a clear error for overlong annotations | Create/update consistency |
| [US-16](#us-16) | Find annotations containing text | A bounded text filter |
| [US-17](#us-17) | Understand invalid annotation date ranges | Validation before querying |
| [US-18](#us-18) | Find event names by prefix | Searchable registry data |
| [US-19](#us-19) | Filter property definitions by type | An allowlist and a query condition |
| [US-20](#us-20) | List active or inactive feature flags | Optional booleans |
| [US-21](#us-21) | Find feature flags by key prefix | Reusing a simple filter pattern |
| [US-22](#us-22) | Page through dead letters | Offset paging and deterministic ordering |
| [US-23](#us-23) | Find failures containing an error phrase | Project-scoped diagnostics |
| [US-24](#us-24) | Open one dead letter by ID | One detail endpoint |
| [US-25](#us-25) | Understand reversed event-export date ranges | A bounded fix in an existing route |

US-01 through US-06 are small script/browser contributions. US-07 onward
use the existing HTTP and SQLite patterns. All can be attempted independently;
where two stories touch the same code, preserve the earlier story's behavior.
No story requires a schema migration, new package, new service deployment,
worker redesign, or changes to the feature-flag hashing algorithm.

## The routine to use for every story

1. Run `git status --short` and inspect the relevant file's existing diff.
   This checkout already contains work. Identify it before adding your own.
2. Open the listed source file and the listed example test or guide. Find
   the exact handler/function named in the story.
3. Write the expected example in your notes. For an API task, write the
   method, URL, input, status code, and response shape before coding.
4. For behavior requiring automated verification, add the smallest useful
   failing case first. Confirm why it fails. A compile error or blocked
   assembly is not a failing behavior assertion.
5. Implement one numbered step, then inspect your change. You do not need
   to predict or edit the whole repository at once.
6. Run the focused checks and inspect actual returned values or saved data.
   Complete the relevant [testing policy](../docs/testing.md) before merging.
7. Update the indicated documentation. Describe the new input, its default,
   and its failure behavior. Do not advertise a proposed story as finished.
8. Write a short review note: problem, change, verification result, and any
   remaining limitation. Explain the story's final question in your own words.

The earlier implementation has an [unresolved validation record](../docs/learning/validation.md),
including a Windows Application Control block and a large-batch timeout.
These plans do not resolve those issues. If they prevent a check, record
"not verified" with the error rather than treating the story as passing.

## A reusable test setup, explained once

Use [PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs) for HTTP tests
and [TestAuth](../tests/Pulse.Tests/TestAuth.cs) to register/authenticate callers.
The factory supplies a private SQLite test database, not your local app's database.

- For project-list tests, create a fresh client and fresh authenticated user
  inside each test so that older tests' projects do not pollute the result.
- For project-scoped tests, create a fresh project for each test. Read
  `ProjectResponse` to get its real ID and keys; placeholders are not valid IDs.
- For a non-member test, create another client, authenticate it as a new
  user, and do not add it to the project's members.
- For a cross-project-data test, create A and B, optionally under the same
  owner, and put similar records in both. A's query must return only A's data.
- If you create people or registry entries through `/capture`, use the
  existing `TestIngestion.WaitForDrainAsync` before asserting processed data.
- For exact time/order fixtures, create the records through the API, then
  use a test service scope to assign explicit entity timestamps and save.
  Do not add sleeps to make timestamps different.
- For dead-letter fixtures, follow `SeedLetterAsync` in
  [IngestionOperationsTests](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs).
  Insert failure records directly in the isolated test database; you do not
  need to damage a real queue or wait through retries.

For API stories, preserve the existing credential policy. These project
management routes require membership through a JWT or personal token;
project read/write keys alone are insufficient. A missing management
identity produces 401, while a signed-in non-member gets 404. Put new
validation after the existing authorization guard unless the story explains
framework-level binding behavior separately.

## Small query conventions used below

**Filter before paging:** start with the existing project/member-scoped
query, add optional `Where` conditions, apply ordering, then `Skip`/`Take`,
then materialize/project the response. Filtering after taking a page can
hide matching records that were outside the unfiltered page.

**Define text behavior:** substring stories use literal, case-sensitive
`Contains` unless specified otherwise. Prefix stories specify ASCII
case-insensitive matching by normalizing the input and queried field.
Do not promise complete Unicode case folding. Keep user input as a query
parameter; do not construct SQL strings or use a regex search.

EF's SQLite provider translates common string operations such as `Contains`,
`StartsWith`, and `ToLower` into SQL. Use supported operations and verify
matching against real SQLite rather than assuming an in-memory comparison
behaves identically. [Provider function mappings](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/functions)
are the reference if translation is unclear.

Length limits below use the trimmed C# string's `Length`, or PowerShell's
equivalent, rather than a promise about user-perceived Unicode characters.

---

<a id="us-01"></a>
## US-01 — Choose the event name used by the learning demo

**User story:** As a learner, I want to supply my own event name to the demo
so I can connect the sample with an action in a product I understand.

**Current starting point:** the demo sends and queries the hardcoded name
`learning_pageview`. **Proposed scope:** one optional parameter; no API changes.
Prerequisite: recognize a PowerShell parameter and a variable.

**Open:** [learning-demo.ps1](../scripts/learning-demo.ps1) and
[first-session instructions](../docs/learning/01-first-session.md).

**Target example:** `./scripts/learning-demo.ps1 -EventName signup` sends two
`signup` events and verifies count 2, unique persons 1. Omitting the option
keeps `learning_pageview`.

### Implementation steps

1. Locate `param(...)` at the start of the script. Add an optional string
   parameter called `EventName`, defaulting to `learning_pageview`.
2. Before the first `Invoke-Pulse` call, trim the parameter and reject blank
   values or a trimmed value longer than 200. Use a message naming EventName.
3. Search this file for every occurrence of `learning_pageview`. Identify
   the event payload occurrences separately from the trend URL occurrence.
4. Use the normalized parameter in both event payloads. Keep the existing
   timestamp, distinct ID, and properties unchanged.
5. URL-encode the name with `[uri]::EscapeDataString` before placing it in
   the trend query. Use the ordinary, unencoded name in JSON payloads.
6. Keep the count assertions at two events and one person. A name change
   should not change what those counts mean.
7. Update narration that explicitly calls these pageviews, and add one
   custom-name invocation to the first-session documentation.

### Acceptance and verification

With the local API running, run the script with no option, with `signup`,
and with `'checkout & pay'`. Each should finish its existing verification.
The last example checks that `&` remains part of the event name rather than
starting another query parameter. `'   '` and a 201-character name must fail
before creating an account/project or sending any HTTP request.

Use a manual script check; a new test framework is unnecessary. If US-02 is
also implemented, the expected count comes from EventCount rather than 2.

**Common mistake:** changing the captured name while querying the old name.
**Done when:** default and custom runs agree with their expected counts and
invalid input fails early. **Explain:** why is URL encoding needed for the
query value but not pre-applied to the JSON event name?

---

<a id="us-02"></a>
## US-02 — Choose how many events the demo sends

**User story:** As a learner, I want to send a small configurable number of
events so I can observe event count changing while unique-person count stays one.

**Current starting point:** the batch contains two literal items.
**Proposed scope:** a count from 1 through 10, default 2. This is not a load test.
Prerequisite: read a loop and understand event count versus person count.

**Open:** [learning-demo.ps1](../scripts/learning-demo.ps1) and
[the data-shapes explanation](05-data-shapes.md).

**Target example:** `./scripts/learning-demo.ps1 -EventCount 5` captures five
matching events for `learner-1`; verification expects 5 events and 1 person.

### Implementation steps

1. Add an integer `EventCount` parameter with default 2 and a validation range
   of 1–10 in the script's parameter block.
2. Find the literal two-element batch. Replace only its construction with
   an array containing EventCount event objects.
3. Use the same distinct ID and timestamp for every generated event so
   the expected number of people and time bucket remain simple.
4. Keep the first two page properties `/intro` and `/transactions` for the
   default run. Additional events can use `/practice/3`, `/practice/4`, etc.
5. Preserve the configured EventName if US-01 exists; otherwise retain the
   original event name. This story does not require US-01 first.
6. Replace both hardcoded expected event counts: the capture queued-count
   assertion and the final trend-count assertion. Keep expected persons at 1.
7. Update messages saying "two" so they describe the actual requested count.
8. Add examples for counts 1 and 5 to the first-session guide.

### Acceptance and verification

Manually run with default settings, 1, and 5. Expect event counts 2, 1, and
5 respectively, always with one resolved person. Use a fresh project per
run through the existing script. Run with 0 and 11 and confirm parameter
validation stops execution before HTTP work.

Specifically check the count-1 case: PowerShell must serialize `batch` as
an array containing one event, not a lone object. Use an explicit array
construction if the pipeline unwraps a single item.

**Common mistake:** creating a different distinct ID on every iteration,
which also changes unique-person count. **Done when:** each boundary and
default works and narration matches the run. **Explain:** why does changing
occurrences not necessarily change the number of people?

---

<a id="us-03"></a>
## US-03 — Print useful follow-up routes after the demo

**User story:** As a learner, I want the demo to show where I can inspect
my practice project so I can continue exploring without reconstructing URLs.

**Current starting point:** success output includes the project ID and a
next-reading suggestion. **Proposed scope:** additional local URLs and clear
labels. No saved credential file and no extra network requests.

**Open:** [learning-demo.ps1](../scripts/learning-demo.ps1),
[keys and permissions](09-keys-and-permissions.md), and
[the ingestion runbook](../docs/runbooks/ingestion.md).

**Target example:** after verification, print the project-detail, project
ingestion-metrics, and project dead-letter-list URLs using the actual BaseUrl
and newly created project ID. Label them "requires a member Bearer token".

### Implementation steps

1. Find the final success messages and the existing `$base`/`$projectPath`
   variables. Note that `$base` already removes a trailing slash.
2. Add a clearly labeled project-detail URL built from those variables.
3. Add the `/ingestion/metrics` URL under that same project path.
4. Add `/ingestion/dead-letters?limit=20` under the same project path.
5. Explain in one output line that these are authenticated API routes;
   pasting a URL into a browser without credentials may yield 401.
6. Do not print `$auth`, the management headers, passwords, or project keys.
   The existing script keeps its generated credentials within the run.
7. Keep all route output after successful verification so an early failure
   is not presented as a completed learning session.
8. Update the demo instructions to explain what the three URLs are for and
   that the generated session token is not exported for later use.

### Acceptance and verification

Run with the default local URL and with another local port where the API
is listening. All three links must use that run's port and project ID;
there must be no double slash between host and path. Compare path shapes
with the source routes rather than assuming names from memory.

Read the printed output once with credentials in mind: it must contain no
JWT, `pk_live_`, `rk_live_`, generated password, or authorization-header value.
Use the existing runbook's separately supplied member token for later manual
requests; do not extend this story into credential persistence.

**Common mistake:** hardcoding port 5141 despite BaseUrl being configurable.
**Done when:** the URLs are correct, labeled, and credential-free.
**Explain:** why is a useful route different from an authenticated request?

---

<a id="us-04"></a>
## US-04 — Restart the visual walkthrough from the beginning

**User story:** As a learner, I want a restart button so I can repeat the
lesson without remembering which controls I changed.

**Current starting point:** the page has previous/next buttons, a scenario
selector, three views, and quiz feedback. **Proposed scope:** one reset action
for existing in-memory state; no saved browser progress.

**Open:** [index.html](index.html). Find `step`, `view`, `go`, `render`, the
scenario select, and the `.navigation` controls.

**Target behavior:** "Start over" selects the success scenario, step 1,
story view, no chosen quiz answer, and no feedback text.

### Implementation steps

1. Add a real `button` with `type="button"`, a unique ID, and visible text
   `Start over` near the existing navigation controls.
2. Find where other buttons register click listeners. Register this button
   alongside them so initialization stays easy to follow.
3. In its handler, set the scenario select's value to `success`.
4. Set `view` to `story`. Use the existing navigation/render path to go to
   step index 0; do not create another independent copy of the lesson DOM.
5. Confirm that `render()` clears selected answers and feedback. Reuse that
   behavior rather than manually clearing some fields and missing others.
6. Keep the existing focus behavior that puts keyboard focus on the step
   heading after navigation. Check that the restart action does the same.
7. Add one sentence to the AstraDocs reading instructions describing restart.

### Acceptance and verification

Open the HTML file locally. Choose the failure scenario, move to step 8,
select code view, answer its question, and request feedback. Press Start over.
Verify step 1, success scenario, story selected, empty feedback, no selected
answer, Previous disabled, and Next enabled. Press it again: the state
should remain valid, with no duplicate steps or controls.

Reach the button using Tab and activate it with Enter. Repeat at a narrow
window width and confirm the control is visible without horizontal scrolling.
Use browser/manual checks; no backend test is required.

**Common mistake:** resetting the step but leaving the failure scenario or
code view active. **Done when:** the complete reset works with mouse and
keyboard. **Explain:** which state belongs to the lesson and which state is
recreated by `render()`?

---

<a id="us-05"></a>
## US-05 — Show the lesson quiz only when the learner is ready

**User story:** As a learner, I want to hide the question while reading the
explanation so I can focus on one part of the lesson at a time.

**Current starting point:** every step always displays a quiz fieldset.
**Proposed scope:** a show/hide button with accessible state. No quiz scoring
or change to the questions themselves.

**Open:** [index.html](index.html). Find the quiz `fieldset`, `choices`,
`feedback`, `render`, and the perspective-view listeners.

**Target behavior:** a new step starts with its quiz hidden. "Show question"
reveals it; "Hide question" hides it. Changing story/code/data view within
the same step preserves whether the quiz is open.

### Implementation steps

1. Give the quiz fieldset a unique ID. Add a real toggle button immediately
   before it; keep the button outside the hidden fieldset.
2. Set the button's `aria-controls` to the fieldset ID and keep its
   `aria-expanded` value synchronized with visible/hidden state.
3. Use the fieldset's `hidden` property to control visibility. Do not rely
   only on a color or move the content off-screen while leaving it focusable.
4. Add a small helper that sets visibility, button label, and expanded state
   together. One helper avoids three values drifting apart.
5. Call that helper with the hidden state inside `render()`, which runs on
   step/scenario changes. A perspective change uses `renderPerspective()`
   and should not reset quiz visibility.
6. On a hide action, clear the chosen answer and feedback, then keep focus
   on the visible toggle. On reopen, the learner gets a fresh attempt.
7. Add the toggle explanation to the visual walkthrough instructions.

### Acceptance and verification

Check initial load, opening, answering, hiding, and reopening. Verify a
fresh answer after reopen, hiding after Next or scenario change, and keeping
the quiz open when only changing explanation view. If US-04 is implemented,
Start over must also end with the quiz hidden through the shared render path.

Tab through the page: hidden radio buttons and Check my reasoning must not
receive focus. Inspect `aria-expanded` while toggling and verify its value
matches what you can see. Use both mouse and keyboard.

**Common mistake:** hiding the fieldset together with its own toggle button.
**Done when:** visibility, focus, and accessibility state stay consistent.
**Explain:** why does a view change have a different reset policy from a
step change?

---

<a id="us-06"></a>
## US-06 — Explain three unfamiliar terms inside the visual lesson

**User story:** As a learner, I want a small expandable glossary beside the
walkthrough so I can check a word without leaving my place.

**Current starting point:** the page links to longer Markdown guides but
has no inline glossary. **Proposed scope:** three native HTML disclosures
for Project, Person, and QueuedEvent. No new JavaScript behavior is necessary.

**Open:** [index.html](index.html), [people and tables](08-people-and-tables.md),
and [the desk reference](17-desk-reference.md).

**Target example:** expand "What is a Person?" and read a two-sentence
explanation with the recurring shop-visitor example.

### Implementation steps

1. Locate a place after the walkthrough or beside its navigation where a
   short `Need a word explained?` section fits without interrupting each step.
2. Add a heading using the existing heading hierarchy.
3. Add one `details` element per term, each with a `summary` question.
   Native details/summary provides keyboard-operable disclosure behavior.
4. Explain Project as the grouping of analytics data and access; use Mara's
   shop as the concrete example.
5. Explain Person as a tracked product visitor. Explicitly distinguish it
   from User, the management operator, in a short second sentence.
6. Explain QueuedEvent as saved pending work. Say that pending work is not
   itself a successfully processed AnalyticsEvent.
7. Add a local link to the relevant guide for each term. Follow the page's
   existing spacing and colors rather than inventing a separate widget style.
8. Keep the glossary outside DOM sections replaced by `render()` so moving
   through the lesson does not unexpectedly close a definition.

### Acceptance and verification

Open and close all three definitions with mouse and keyboard. Leave Person
open, move to another step, and verify it stays open. At a narrow width,
the text and links must wrap without hiding the main lesson controls.

Disable JavaScript and reload: the glossary disclosures must still work,
while the page's existing no-script guidance remains available. Check each
local link points to an existing Markdown file.

**Common mistake:** adding a second definition that contradicts the original
User/Person distinction. **Done when:** the wording matches source entities,
the disclosures work without JavaScript, and the layout remains readable.
**Explain:** why is a native HTML element enough for this behavior?

---

<a id="us-07"></a>
## US-07 — Reject project names longer than 200 characters

**User story:** As a project creator, I want a clear validation error for an
overlong name so I can correct it before storing inconsistent project data.

**Current starting point:** project creation rejects blank names and trims
valid ones. The EF model declares a 200-character maximum, but the HTTP
handler has no explicit length check. **Proposed scope:** create validation
only, with no database/schema change.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[ProjectContracts.cs](../src/Pulse.Api/Contracts/ProjectContracts.cs),
[ProjectEndpointsTests.cs](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs),
and [PulseDbContext.cs](../src/Pulse.Infrastructure/PulseDbContext.cs).

**Target behavior:** `POST /api/projects` accepts a trimmed name of length
1–200. A trimmed name of length 201 returns 400 with an `errors.name` entry.

### Implementation steps

1. Find the project `MapPost("/")` handler and its existing blank-name guard.
2. Add a test that posts an authenticated request containing 201 `a`
   characters. Assert 400 and a `name` validation error.
3. Normalize the name once using trim, with a null-safe path for malformed
   or missing input. Preserve the existing required-name behavior.
4. Add a 200 maximum constant in the endpoint class if that keeps the check
   readable; do not create a new validation framework for one field.
5. Return `Results.ValidationProblem` for excessive length before adding
   either Project or ProjectMembership entities.
6. Assign the normalized validated value when creating the project.
7. Add acceptance tests for exactly 200 characters, padded valid input, and
   blank input. Verify an invalid request creates no project for a fresh caller.
8. Document the trimmed 1–200 rule in the API reference's project section.

### Acceptance and verification

Test 200 characters -> 201 Created; 201 -> 400; `"  Shop  "` -> stored `Shop`;
whitespace -> 400. Padding around an otherwise 200-character name remains
valid because the rule measures the trimmed value. An anonymous request
continues to receive 401 through the existing route authorization.

Run `dotnet test --filter "FullyQualifiedName~ProjectEndpointsTests"`.
No test should depend on the database truncating a string.

**Common mistake:** assuming `HasMaxLength` is a complete HTTP validation
policy. **Done when:** boundaries, error shape, and no-write behavior are
verified. **Explain:** why do you validate before adding the membership row?

---

<a id="us-08"></a>
## US-08 — Rename an existing project

**User story:** As a project member, I want to rename a project so its label
can stay useful when my product name changes.

**Current starting point:** project routes support create, list, fetch, and
membership operations, but no rename route. **Proposed scope:** one name-only
update. Existing membership policy applies; do not invent an owner role.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[ProjectContracts.cs](../src/Pulse.Api/Contracts/ProjectContracts.cs),
[ProjectEndpointsTests.cs](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs),
and [ProjectAccessService.cs](../src/Pulse.Api/Auth/ProjectAccessService.cs).

**Target example:** `PUT /api/projects/{id}` with `{"name":"New Shop"}`
returns 200 with the existing `ProjectResponse` shape and the new name.

### Implementation steps

1. In ProjectContracts, plan an `UpdateProjectRequest` containing nullable
   `Name`. The rename operation requires a provided, nonblank value.
2. Add a test: create `Old Shop`, remember its response, rename it, then
   fetch it with the existing GET route and assert `New Shop`.
3. Map a PUT handler for `/{id:guid}` in the existing project route group.
   Add the request, HTTP context, context, access service, and cancellation
   token parameters by following the nearby GET-by-ID pattern.
4. Call `RequireMemberAsync` first. Return its denial immediately.
5. Load the project using the authorized ID; return 404 if it is absent.
6. Trim and require a name of length 1–200. Reuse US-07's constant if present;
   otherwise implement this endpoint's check without depending on US-07.
7. Change only `Name`, save with the cancellation token, and return the
   existing `ToResponse(project)` output.
8. Add tests for invalid input, unchanged keys/ID/CreatedAt, and a non-member
   caller. Document the new route, statuses, and request field.

### Acceptance and verification

Verify a normal rename, padded input, repeating the same rename, blank and
overlong input, unauthenticated caller (401), and signed-in non-member (404).
An invalid rename must leave the stored old name intact. IDs, membership,
write/read keys, and creation timestamp must not change after a valid rename.

Run `dotnet test --filter "FullyQualifiedName~ProjectEndpointsTests"`.
Use a fresh stranger client for denial cases rather than clearing the owner's
token and accidentally testing anonymous access instead.

**Common mistake:** constructing a new Project, which generates a different
identity or keys. **Done when:** the persisted label changes and the other
fields remain intact. **Explain:** how is renaming different from recreating?

---

<a id="us-09"></a>
## US-09 — Filter the project list by a name fragment

**User story:** As a member of several projects, I want to search their names
so I can find the right workspace without scanning every page.

**Current starting point:** the project list joins projects to the caller's
memberships, orders by creation time, and pages the result. **Proposed scope:**
optional `nameContains`, with literal case-sensitive substring matching.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs)
and [ProjectEndpointsTests.cs](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs).

**Target example:** the caller owns `Shop API`, `Shop SDK`, and `Internal`.
`GET /api/projects?nameContains=Shop` returns the two Shop projects.

### Implementation steps

1. Create a fresh authenticated test client and those three projects. Add
   a test asserting exact returned project IDs for the filtered request.
2. Add nullable string `nameContains` to the list handler's parameters.
3. Trim it; omitted/blank means no filter. Reject a trimmed value longer
   than 200 with 400 and an `errors.nameContains` entry.
4. Split the existing query expression from its terminal `ToListAsync`
   call so you have a query variable to extend.
5. Keep the membership join and caller condition in that base query. Add
   `Where` using the project's Name and the normalized term when nonempty.
6. Apply the existing creation ordering and pagination after the filter.
   Preserve the array response and existing limit/offset defaults.
7. Add cases for no match, blank/omitted input, case difference, and filtering
   before pagination. Keep any sort option added by US-10 intact.
8. Add another user's matching project and prove it is absent. Update the
   API reference with the parameter, case policy, and length bound.

### Acceptance and verification

`Shop` matches the two named projects; `shop` does not under this story's
case-sensitive rule. `missing` returns `[]`, not 404. `Shop&limit=1` returns
one matching project even when an unrelated project was created earlier.
Whitespace behaves as omission; 201-character input returns 400.

Run `dotnet test --filter "FullyQualifiedName~ProjectEndpointsTests"`.
Use returned IDs, not only counts, so a wrong matching project cannot pass.

**Common mistake:** applying the search after `Skip`/`Take` or removing the
membership join during refactoring. **Done when:** matching, bounds, paging,
and caller isolation are verified. **Explain:** why can filtering one fetched
page produce an incomplete search result?

---

<a id="us-10"></a>
## US-10 — Choose oldest-first or newest-first project ordering

**User story:** As a project member, I want newest projects at the top when
I am working on a recent project so it is easier to locate.

**Current starting point:** project listing orders by CreatedAt ascending.
**Proposed scope:** one optional sort value, not arbitrary field selection.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[ProjectEndpointsTests.cs](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs),
and [Project.cs](../src/Pulse.Domain/Entities/Project.cs).

**Target contract:** `sort=oldest` and `sort=newest`; omitted or blank means
oldest. Trim and normalize ASCII case. Any other nonblank value returns 400
with `errors.sort`.

### Implementation steps

1. Create a fresh user and three projects. In a test scope, assign fixed
   CreatedAt values on three different days, using the returned project IDs.
2. Add nullable `sort` to the existing list handler and normalize it before
   choosing ordering. Preserve the existing route authorization.
3. Keep the base membership query as an unordered query variable; preserve
   any name filter already added by US-09.
4. For oldest, order by CreatedAt ascending then Id ascending. For newest,
   order by CreatedAt descending then Id descending.
5. Apply `Skip` and `Take` after the chosen ordering. Return the same response
   shape as before, using the existing conversion helper.
6. Add exact ID-sequence tests for both modes and paging across distinct days.
7. Add an equal-timestamp test. Fetch twice and assert the same order, and
   check adjacent pages do not overlap with an unchanged fixture. Avoid
   assuming in-memory Guid ordering is identical to SQLite's ordering.
8. Document the accepted values and tie-break behavior.

### Acceptance and verification

With projects A/B/C on successive days, oldest returns A/B/C and newest
returns C/B/A. `sort=newest&limit=1&offset=1` returns B. Omission and blank
retain oldest-first behavior. `sort=random` returns 400 rather than silently
falling back. A matching project belonging only to another user stays absent.

Run `dotnet test --filter "FullyQualifiedName~ProjectEndpointsTests"`.
Fixed fixture times must be assigned directly in test storage rather than
by pausing between HTTP requests.

**Common mistake:** calling `OrderBy` a second time where `ThenBy` was
intended, replacing the primary ordering. **Done when:** both orders, stable
ties, invalid input, and paging are verified. **Explain:** why is a tie-breaker
helpful even when most project creation times differ?

---

<a id="us-11"></a>
## US-11 — Find project members by email fragment

**User story:** As a project member, I want to filter the member list by
email so I can confirm whether a colleague already has access.

**Current starting point:** the member-list route joins memberships to
users and supports paging. Emails are normalized on account creation.
**Proposed scope:** optional `emailContains`; no invitation or membership changes.

**Open:** [ProjectEndpoints.cs](../src/Pulse.Api/Endpoints/ProjectEndpoints.cs),
[AuthContracts.cs](../src/Pulse.Api/Contracts/AuthContracts.cs),
[TestAuth.cs](../tests/Pulse.Tests/TestAuth.cs), and
[ProjectEndpointsTests.cs](../tests/Pulse.Tests/Api/ProjectEndpointsTests.cs).

**Target example:** `GET /api/projects/{id}/members?emailContains=ALPHA`
finds a member whose stored email includes `alpha`, regardless of ASCII case.

### Implementation steps

1. Create a project owner, register two additional users with unique emails
   containing `alpha` and `beta`, and add them through the existing members POST.
2. Add a test expecting only the alpha member from the filtered GET. Choose
   addresses that do not accidentally match the owner's random email.
3. Add nullable string `emailContains` to the members GET handler, not the
   POST handler. Keep `RequireMemberAsync` first.
4. Trim and lowercase the search input with `ToLowerInvariant`. Treat blank
   as omitted and reject a trimmed value longer than 320 with `errors.emailContains`.
5. In the existing join query, retain the membership project predicate and
   add a condition on the joined user's normalized Email when a term exists.
6. Preserve the existing `MemberResponse` projection, creation ordering,
   and pagination. Perform filtering before paging.
7. Register an extra matching user who is not a member. Assert that merely
   having an account never makes them appear in this list.
8. Add documentation for case handling, omission, length, and auth.

### Acceptance and verification

Lowercase and uppercase alpha searches return the same member. A whitespace
search returns the unfiltered member list. An unknown fragment returns an
empty array. More than 320 trimmed characters returns 400. Add enough matching
members to verify `limit=1` pages the matches, rather than the original list.

A signed-in non-member cannot search this project's membership and receives
404. Verify no memberships were created by any GET request.
Run `dotnet test --filter "FullyQualifiedName~ProjectEndpointsTests"`.

**Common mistake:** querying Users directly and returning every matching
account. **Done when:** only actual members are returned and normalization
and paging are verified. **Explain:** why do you still need the membership
join when the email uniquely identifies a user?

---

<a id="us-12"></a>
## US-12 — Filter the people list by an exact distinct ID

**User story:** As a project member investigating a visitor, I want to use
an exact distinct-ID filter on the people list so I can keep using its array
response while narrowing the result to that visitor.

**Current starting point:** there is already a dedicated by-distinct-ID
detail route; the list has only limit/offset. **Proposed scope:** add an
optional list filter and preserve the existing detail route.

**Open:** [PersonEndpoints.cs](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[PersonContracts.cs](../src/Pulse.Api/Contracts/PersonContracts.cs),
[PersonDistinctId.cs](../src/Pulse.Domain/Entities/PersonDistinctId.cs), and
[IdentityTests.cs](../tests/Pulse.Tests/Api/IdentityTests.cs).
Use a new `tests/Pulse.Tests/Api/PersonListTests.cs` if no dedicated list test
class exists; copy the factory/client setup pattern, not unrelated tests.

**Target example:** `GET /api/projects/{projectId}/persons?distinctId=device-7`
returns a one-element array containing the Person mapped to `device-7`.

### Implementation steps

1. Prepare two people through captures with distinct IDs `device-7` and
   `device-8`, then wait for ingestion. Add a filtered-list expectation.
2. Add nullable string `distinctId` to the list handler after reviewing its
   existing membership guard.
3. Trim input; blank means no filter. Reject more than 400 trimmed characters
   with 400 and an `errors.distinctId` entry. Keep matching case-sensitive.
4. Start from `Persons.Where(p => p.ProjectId == projectId)` as before.
5. Add an `Any` condition over PersonDistinctIds that checks project ID,
   current person's ID, and exact normalized distinct ID. Keep it within the
   database query so one Person cannot appear repeatedly through a join.
6. Apply the existing CreatedAt ordering, paging, and `ToResponseAsync` mapping.
7. Add a same-text distinct ID in another project and prove it is excluded.
   Add an alias to one person through `$identify` and verify one list result.
8. Document that no match returns `[]`, whereas the separate detail route
   may return 404. If US-13 exists, combine the filters with AND.

### Acceptance and verification

Assert matching ID -> one correct Person; unknown ID -> empty array;
omitted/blank -> ordinary list; case mismatch -> no match; excessive length
-> 400. An `offset=1` request for one matched person returns an empty array.
Keep the dedicated by-distinct-ID endpoint unchanged.

Run `dotnet test --filter "FullyQualifiedName~PersonListTests|FullyQualifiedName~IdentityTests"`.
**Common mistake:** using a join that duplicates a person with several aliases.
**Done when:** exact matching, aliases, paging, and project isolation hold.
**Explain:** how does an existence check differ from returning every joined row?

---

<a id="us-13"></a>
## US-13 — Filter people by when their records were created

**User story:** As a project member, I want to list people created during a
selected period so I can inspect recently observed visitors.

**Current starting point:** Person has CreatedAt, but listing cannot filter
it. **Proposed scope:** `createdFrom` inclusive and `createdBefore` exclusive.
These refer to person record creation, not the timestamp of the person's events.

**Open:** [PersonEndpoints.cs](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[Person.cs](../src/Pulse.Domain/Entities/Person.cs), and
[DateTimeOffsetConversionTests.cs](../tests/Pulse.Tests/Infrastructure/DateTimeOffsetConversionTests.cs).
Add/reuse the proposed `tests/Pulse.Tests/Api/PersonListTests.cs` using
[IdentityTests](../tests/Pulse.Tests/Api/IdentityTests.cs) as a setup example.

**Target example:** `?createdFrom=2026-09-08T00:00:00Z&createdBefore=2026-09-09T00:00:00Z`
returns people created on September 8 in UTC.

### Implementation steps

1. Create people in one test project and assign fixed CreatedAt values in
   a test scope: just before start, exactly start, inside, and exactly end.
2. Add nullable `DateTimeOffset` parameters for both new query names.
3. After membership authorization, reject a range with both values present
   and start greater than or equal to end. Return a validation error naming
   `createdFrom` and explaining that it must precede `createdBefore`.
4. Retain the base project predicate. Add `CreatedAt >= start` when the
   lower bound exists, and `CreatedAt < end` when the upper bound exists.
5. Apply conditions before the existing order/paging/response mapping. Keep
   any exact distinct-ID filter from US-12 and combine it with AND.
6. Add lower-only, upper-only, and omitted-bounds tests.
7. Add a test expressing the same instants with a nonzero UTC offset. URL-
   encode timestamp query values so a plus sign is not interpreted as a space.
8. Document the half-open interval using one included start and excluded end.

### Acceptance and verification

The main fixture returns exactly the start and inside records. End/start
equality and reversed bounds return 400. A single valid bound is allowed;
omission preserves the existing list. Invalid timestamp text returns 400
through request binding. Other projects' people never appear.

Run `dotnet test --filter "FullyQualifiedName~PersonListTests"`.
**Common mistake:** using an event's client timestamp as Person.CreatedAt
in the fixture without actually setting the person record's time.
**Done when:** both boundaries, offsets, optional bounds, and scoping pass.
**Explain:** why does an exclusive upper bound help split adjacent periods?

---

<a id="us-14"></a>
## US-14 — Return a project's total person count

**User story:** As a project member, I want to see the total number of tracked
people so I do not have to fetch every page merely to count them.

**Current starting point:** people can be listed and fetched; there is no
dedicated count route. **Proposed scope:** all Person records in this project,
with no optional search/date filters and no count of management Users.

**Open:** [PersonEndpoints.cs](../src/Pulse.Api/Endpoints/PersonEndpoints.cs),
[PersonContracts.cs](../src/Pulse.Api/Contracts/PersonContracts.cs), and
[IdentityTests.cs](../tests/Pulse.Tests/Api/IdentityTests.cs).
Add/reuse `tests/Pulse.Tests/Api/PersonListTests.cs` for verification.

**Target contract:** `GET /api/projects/{projectId}/persons/count` returns
200 with `{"count":3}` when the project contains three Person records.

### Implementation steps

1. Write an empty-project test expecting `count` equal to zero.
2. Add a `PersonCountResponse` record containing an integer Count to the
   existing PersonContracts file. Keep it separate from paginated person data.
3. Add a GET `/count` mapping inside the existing people route group. The
   current `/{personId:guid}` constraint helps distinguish detail routes.
4. Follow neighboring handler parameters and call `RequireMemberAsync`
   before querying. Return any denial immediately.
5. Use `db.Persons.CountAsync` with a project predicate and cancellation token.
   Do not load person rows or distinct-ID lists to calculate the count.
6. Return the count response with `Results.Ok`.
7. Capture three events using IDs A, A, and B; after draining, assert count 2.
   Add an event in another project and verify the first count stays 2.
8. Test anonymous, read-key-only, write-key-only, and signed-in non-member
   callers. Document the route and that it counts stored Person records.

### Acceptance and verification

Empty project -> 0; two resolved people -> 2; repeated events by one person
do not add a person. Management account creation alone does not change the
count. No database writes or list pagination are needed by this endpoint.
The count is a current observation and may change after later ingestion.

Run `dotnet test --filter "FullyQualifiedName~PersonListTests"`.
Inspect response JSON for the single `count` field and expected auth statuses.

**Common mistake:** counting events or distinct-ID mappings instead of people.
**Done when:** zero, repeated-identity, project-isolation, and credential cases
are verified. **Explain:** why can three event rows correspond to two people?

---

<a id="us-15"></a>
## US-15 — Enforce a 2,000-character annotation limit consistently

**User story:** As a project member writing a dated note, I want the API to
reject an overlong annotation clearly so I can shorten it before saving.

**Current starting point:** creation requires content; update rejects a
provided blank value. Both trim content. The model declares a 2,000 length,
but neither handler explicitly checks it. **Proposed scope:** validation on
both existing write routes, with their partial-update behavior preserved.

**Open:** [DataManagementEndpoints.cs](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[DataManagementContracts.cs](../src/Pulse.Api/Contracts/DataManagementContracts.cs),
and [DataManagementTests.cs](../tests/Pulse.Tests/Api/DataManagementTests.cs).
Find `MapAnnotations`; person deletion elsewhere in that file is unrelated.

**Target behavior:** trimmed content up to 2,000 is accepted; 2,001 returns
400 with `errors.content`. On update, omitted/null content keeps the old text.

### Implementation steps

1. Add a creation test with 2,001 characters and the required valid date.
   Expect validation failure and no added annotation.
2. Add a small maximum-content-length constant near the endpoint class.
3. In creation validation, measure a null-safe trimmed value. Preserve
   required date and required content errors, and reject excessive length.
4. In update, keep membership and project-scoped annotation lookup first.
   Continue returning 404 when that annotation is unavailable.
5. Validate the trimmed content only when the caller supplied non-null
   content. Do not accidentally make a date-only update require text.
6. Complete every validation check before assigning either Date or Content.
   An invalid content field must not partly apply a supplied new date.
7. Save using the existing flow and response mapping. Add exact-boundary,
   padded-content, date-only, and invalid-update persistence tests.
8. Update the annotation API reference for both POST and PUT.

### Acceptance and verification

POST and PUT accept 2,000 trimmed characters and reject 2,001. Padding is
trimmed before measurement. A valid date-only PUT keeps existing text. A PUT
with a new date plus overlong content returns 400; a later GET/list shows
both the old date and old content unchanged.

Run `dotnet test --filter "FullyQualifiedName~DataManagementTests"`.
Keep existing blank-input and membership behavior covered.

**Common mistake:** validating after changing tracked entities, or applying
the create-required rule to omitted update fields. **Done when:** create and
update agree on length while partial updates still work. **Explain:** why
must validation finish before assigning the annotation's new date?

---

<a id="us-16"></a>
## US-16 — Search annotation content for a phrase

**User story:** As a project member, I want to find notes containing a word
such as `launch` so I can locate relevant changes in my analytics timeline.

**Current starting point:** annotation listing already supports date range
and pagination. **Proposed scope:** optional literal, case-sensitive
`contentContains`, combined with the existing date filters using AND.

**Open:** [DataManagementEndpoints.cs](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs)
inside `MapAnnotations`, and
[DataManagementTests.cs](../tests/Pulse.Tests/Api/DataManagementTests.cs).

**Target example:** `GET /api/projects/{id}/annotations?contentContains=launch`
matches `Mobile launch` and `Website launch`, but not `Pricing update`.

### Implementation steps

1. Create those three annotations in a fresh test project with explicit dates.
   Add an exact-ID assertion for the two matching records.
2. Add nullable string `contentContains` to the annotation GET handler.
3. After the membership guard, trim the term. Blank means no filter; more
   than 100 trimmed characters produces 400 and `errors.contentContains`.
4. Locate the existing `query` variable, which already limits ProjectId.
   Add a Content.Contains condition for a nonempty term.
5. Keep date bounds on the same query. Retain ordering by Date then CreatedAt,
   followed by existing Skip/Take and response projection.
6. Add a test where the earlier unfiltered row does not match and `limit=1`
   must still find the first matching row.
7. Add a combined date/text case and a matching note in another project.
   Keep any range validation from US-17 if already implemented.
8. Document literal matching, case sensitivity, maximum term length, and
   combination with date filters.

### Acceptance and verification

Verify the main example, `Launch` versus `launch`, no matches, omission,
whitespace, and overlong input. A search containing `%` must look for a
literal percent sign, not match every note. URL-encode special characters
when constructing test or manual query strings.

Run `dotnet test --filter "FullyQualifiedName~DataManagementTests"`.
Check returned IDs/content, an empty-array result for no matches, and no
cross-project records. GET must not change the stored annotations.

**Common mistake:** searching person properties or filtering the already
materialized page instead of the annotation query. **Done when:** text,
date, pagination, and project conditions all work together. **Explain:**
what does AND mean when two optional filters are present?

---

<a id="us-17"></a>
## US-17 — Reject a reversed annotation date range

**User story:** As a project member, I want a useful error when my annotation
start date is after my end date so I can correct the range instead of
mistaking an empty result for missing notes.

**Current starting point:** annotation listing independently applies inclusive
`from` and `to` filters. A reversed pair naturally produces no matches.
**Proposed scope:** validate their relationship; retain inclusive boundaries.

**Open:** [DataManagementEndpoints.cs](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[DataManagementTests.cs](../tests/Pulse.Tests/Api/DataManagementTests.cs), and
[InsightEndpoints.cs](../src/Pulse.Api/Endpoints/InsightEndpoints.cs) for an
existing range-validation style to study.

**Target example:** `?from=2026-09-10&to=2026-09-08` returns 400 with
`errors.from`; `from` equal to `to` remains a valid one-day query.

### Implementation steps

1. Add a test for a signed-in member using a reversed pair. Assert status
   400 and a validation field named `from`, not merely a non-success code.
2. Find the annotation list's membership guard and its first query-building
   lines. Place the new relationship check between them.
3. Check ordering only when both nullable DateOnly values are present.
   Reject start greater than end, not greater than or equal to end.
4. Return a ValidationProblem explaining that `from` must not be after `to`.
   Do not swap dates silently or rewrite the caller's request.
5. Leave both existing date conditions inclusive and preserve pagination
   and any optional content filter from US-16.
6. Add an annotation on September 8 and request that date as both bounds;
   assert that the annotation is returned.
7. Add tests for lower-only, upper-only, and absent bounds. These cases do
   not have a pair to compare and should remain valid.
8. Document the reversed-range error beside the route's date parameters.

### Acceptance and verification

Reversed range -> 400; equal dates -> valid response; ordinary ascending
range -> expected notes. Missing either bound preserves existing behavior.
Malformed date text still returns 400 via binding. A validly bound reversed
range requested by a signed-in non-member still receives the membership
denial before this new handler validation.

Run `dotnet test --filter "FullyQualifiedName~DataManagementTests"`.
**Common mistake:** copying US-13's half-open interval rule and rejecting
equal dates here. **Done when:** the relationship is validated without
changing valid existing ranges. **Explain:** why are equal bounds valid for
this inclusive date query but invalid for US-13's half-open range?

---

<a id="us-18"></a>
## US-18 — Find registered event names by prefix

**User story:** As a project member browsing event names, I want a prefix
filter so I can narrow the registry to related actions such as checkout events.

**Current starting point:** the registry lists observed names alphabetically
with paging. **Proposed scope:** optional `namePrefix`, ASCII case-insensitive,
without changing how ingestion registers event names.

**Open:** [DataManagementEndpoints.cs](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs)
inside `MapDefinitions`, [EventDefinition.cs](../src/Pulse.Domain/Entities/EventDefinition.cs),
and [DataManagementTests.cs](../tests/Pulse.Tests/Api/DataManagementTests.cs).
Read its `Definitions_AutoPopulateOnIngest` test for fixture creation.

**Target example:** names `checkout_started`, `Checkout_finished`, and
`pageview`; `?namePrefix=CHECK` returns the first two with their original spelling.

### Implementation steps

1. Capture one event of each name in a fresh project and drain ingestion.
   Add a filtered registry test with the expected set of names.
2. Add nullable string `namePrefix` to the event-definitions handler only.
   Preserve its membership check.
3. Trim and normalize the input using ToLowerInvariant; blank means no
   filter. Reject more than 200 trimmed characters with `errors.namePrefix`.
4. Split the current fluent query into a base ProjectId-scoped query plus
   its final ordering/paging/projection.
5. Add a prefix condition using the database Name.ToLower() and normalized
   prefix. Use a normal parameterized StartsWith expression, not raw SQL.
6. Keep returning each original stored Name, FirstSeenAt, and LastSeenAt.
   Lowercasing for matching must not rewrite or lowercase the stored name.
7. Test pagination after filtering, omission, and a matching name in another
   project. Add literal underscore/percent fixtures rather than treating
   those characters as search wildcards.
8. Document the ASCII case policy and that this filters observed names only.

### Acceptance and verification

`CHECK` and `check` return the same two names. `checkout_` must match that
literal prefix, not `checkoutX...`; a `%` query searches for a literal percent
prefix. No-match returns `[]`. Blank is unfiltered, and excessive length is
400. Original names and their timestamps remain unchanged.

Run `dotnet test --filter "FullyQualifiedName~DataManagementTests"` against
the real SQLite test host to establish translation and literal matching.

**Common mistake:** adding this filter to CaptureService and preventing
unrelated events from being stored. **Done when:** only registry retrieval
changes and matching/paging/scoping are verified. **Explain:** how does a
registry search differ from an ingestion rule?

---

<a id="us-19"></a>
## US-19 — Filter property definitions by their recorded JSON type

**User story:** As a project member exploring event properties, I want to
list numeric properties so I can find fields that might be useful in analysis.

**Current starting point:** property definitions include PropertyType and
are listed by name. **Proposed scope:** optional `type` filter using the
five recorded labels: string, number, boolean, object, array.

**Open:** [DataManagementEndpoints.cs](../src/Pulse.Api/Endpoints/DataManagementEndpoints.cs),
[PropertyDefinition.cs](../src/Pulse.Domain/Entities/PropertyDefinition.cs),
[DataManagementContracts.cs](../src/Pulse.Api/Contracts/DataManagementContracts.cs),
and [DataManagementTests.cs](../tests/Pulse.Tests/Api/DataManagementTests.cs).

**Target example:** `?type=number` returns `amount` and `duration` definitions
whose recorded type is number, excluding a string property such as `page`.

### Implementation steps

1. Use a capture fixture with ordinary properties representing all five
   types. Avoid system `$...` properties and null-only values, which are
   intentionally not ordinary first-observed registry entries.
2. Add nullable string `type` to the property-definitions GET handler.
3. After authorization, trim and lowercase input. Omitted/blank means all
   types. Validate nonblank input against the explicit five-value allowlist.
4. Return 400 with `errors.type` and the supported values for anything else;
   do not silently interpret `integer` as `number` or `bool` as `boolean`.
5. Add a PropertyType equality predicate to the existing ProjectId-scoped
   query before name ordering and pagination.
6. Preserve the response shape, including original names and seen timestamps.
7. Test every accepted label, an uppercase label, invalid labels, omitted
   input, and data from another project. Add one filtered-page case.
8. Document that the type is first observed metadata, not a fresh scan of
   every historical value or an enforced event schema.

### Acceptance and verification

`number` and `NUMBER` return the same numeric definitions. `integer` returns
400 with a type error. A valid type with no matching definitions returns an
empty array. Blank returns the existing unfiltered list. A later differently
typed value does not get reclassified by this read-only endpoint.

Run `dotnet test --filter "FullyQualifiedName~DataManagementTests"`.
**Common mistake:** inventing a new database type enum or rewriting ingestion
to fit a read filter. **Done when:** allowlist behavior, all five types,
paging, and isolation are verified. **Explain:** why does recorded metadata
not guarantee every event has that property's same type?

---

<a id="us-20"></a>
## US-20 — List only active or only inactive feature flags

**User story:** As a project member managing flags, I want to filter by the
Active setting so I can focus on currently enabled configurations or cleanup.

**Current starting point:** flag listing returns both active and inactive
definitions. **Proposed scope:** one optional boolean filter on the management
list only. Active does not mean a flag evaluates true for every visitor.

**Open:** [FeatureFlagEndpoints.cs](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlagContracts.cs](../src/Pulse.Api/Contracts/FeatureFlagContracts.cs),
[FeatureFlag.cs](../src/Pulse.Domain/Entities/FeatureFlag.cs), and
[FeatureFlagTests.cs](../tests/Pulse.Tests/Api/FeatureFlagTests.cs).

**Target contract:** `GET /api/projects/{id}/feature-flags?active=true` returns
definitions with Active true; false returns only inactive ones; omission
preserves the full list.

### Implementation steps

1. Create two boolean flag definitions with explicit active values true and
   false. Add list tests for both filter values and omission.
2. Find the management list `group.MapGet("/")`, taking care not to edit
   `/decide` or `/local-evaluation` handlers elsewhere in the file.
3. Add nullable boolean `active` to this handler. The null state represents
   "no filter", so do not replace it with a non-nullable false default.
4. Retain membership authorization and the ProjectId predicate.
5. Add an Active equality condition only when the parameter has a value.
   Then apply the existing key order, paging, and response mapping.
6. Add a test with an active flag at 0% rollout. It still appears under
   `active=true`, proving that list filtering is about stored configuration.
7. Check a matching flag in another project, a filtered page, and invalid
   query text such as `active=maybe` through request binding.
8. Document the distinction between Active and a visitor's evaluation result.

### Acceptance and verification

True returns only active definitions; false only inactive; omission returns
both. Invalid nonboolean text produces 400. No flags are changed, and existing
decide/local-evaluation behavior is preserved. A validly formed request by
a non-member still receives 404.

Run `dotnet test --filter "FullyQualifiedName~FeatureFlagTests"` and relevant
existing flag-targeting tests if your edit accidentally touches shared logic.

**Common mistake:** using the flag's rollout result to decide whether it
belongs in the list. **Done when:** the three nullable-boolean states and
project scoping work. **Explain:** why can an Active flag evaluate false?

---

<a id="us-21"></a>
## US-21 — Find feature flags by key prefix

**User story:** As a project member, I want to filter flags by a key prefix
such as `checkout_` so I can inspect the flags associated with one product area.

**Current starting point:** management listing orders by Key and pages all
flags. **Proposed scope:** optional `keyPrefix`, ASCII case-insensitive,
with original key values preserved. US-20 is helpful practice but not required.

**Open:** [FeatureFlagEndpoints.cs](../src/Pulse.Api/Endpoints/FeatureFlagEndpoints.cs),
[FeatureFlagTests.cs](../tests/Pulse.Tests/Api/FeatureFlagTests.cs), and
[FeatureFlagHasher.cs](../src/Pulse.Domain/FeatureFlagHasher.cs) only to
understand why this task must not rewrite stored keys.

**Target example:** keys `checkout_new`, `Checkout_old`, `checkoutXnew`, and
`search_new`; `?keyPrefix=CHECKOUT_` returns only the first two.

### Implementation steps

1. Create the four flag definitions through the existing management API in
   a fresh project. Give them valid boolean types and simple rollouts.
2. Add a test asserting the exact returned key set for `CHECKOUT_`.
3. Add nullable string `keyPrefix` to the management list handler only.
   Preserve the membership guard and any optional active filter from US-20.
4. Trim and lowercase the term. Treat blank as no filter and reject a
   trimmed value longer than 200 with 400 and `errors.keyPrefix`.
5. Extend the ProjectId-scoped query with a Key.ToLower().StartsWith condition
   using the normalized parameter. Apply it before key ordering and paging.
6. Keep `ToResponse` returning the original key. Do not change flag creation,
   update behavior, hash inputs, `/decide`, or local-evaluation payloads.
7. Add lower/uppercase, no-match, literal underscore, and filtered-page tests.
   If US-20 exists, verify both parameters combine using AND.
8. Add the parameter and case/literal-matching policy to the API reference.

### Acceptance and verification

`CHECKOUT_` and `checkout_` return the two underscored checkout flags, with
their original case. `checkoutXnew` is excluded. A `%` prefix returns no
matches for these valid keys; it must not act as a wildcard matching all.
Blank preserves the old list; excessive length is 400; another project's
matching keys are excluded.

Run `dotnet test --filter "FullyQualifiedName~FeatureFlagTests"` using real
SQLite so the prefix and escaping behavior is checked through the API.

**Common mistake:** lowercasing stored keys as part of a search feature,
which can affect deterministic evaluation. **Done when:** filtering changes
retrieval only, including pagination and project isolation. **Explain:**
how can you normalize a comparison without modifying the compared data?

---

<a id="us-22"></a>
## US-22 — Add offset pagination to dead-letter inspection

**User story:** As a project member investigating failures, I want to view
the next page of dead letters so I can inspect more than the first result page.

**Current starting point:** the dead-letter list accepts limit, clamps it
to 1–500, and sorts by FailedAt descending. It has no offset.
**Proposed scope:** offset paging with a deterministic tie-breaker. No
cursor format and no snapshot guarantee during new writes or replays.

**Open:** [IngestionEndpoints.cs](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[DeadLetterEvent.cs](../src/Pulse.Domain/Entities/DeadLetterEvent.cs), and
[IngestionOperationsTests.cs](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs).
Its direct dead-letter seeding helper is the fixture starting point.

**Target example:** `?limit=2&offset=2` skips the first two results and returns
the next two for that project. Omitted offset and negative offset both act as zero.

### Implementation steps

1. Seed five letters for one project with explicit different FailedAt times.
   Use stable IDs in your assertions and a harmless stored payload string.
2. Add a test reading pages at offsets 0, 2, and 4 with limit 2. Expect
   lengths 2, 2, and 1 in newest-first order without duplicate IDs.
3. Add nullable integer `offset` to the existing dead-letter GET handler.
   Preserve the membership guard and existing limit clamping.
4. Normalize offset with the repository's existing nonnegative-offset
   convention: omission -> 0, negative -> 0.
5. Retain ProjectId filtering, order by FailedAt descending, then add Id
   descending as the tie-breaker. Apply Skip before Take, then project the
   existing DeadLetterResponse fields.
6. Add a fixture with equal FailedAt values and fixed IDs. Assert repeated
   reads and adjacent pages are consistent for unchanged data; avoid relying
   on a different in-memory GUID comparer for the expected SQLite order.
7. Add beyond-end and other-project cases. Preserve US-23's filter if present
   and ensure paging comes after that filter.
8. Document the new parameter and explain that offset pages can shift when
   concurrent replay/insertion changes the list.

### Acceptance and verification

Verify the three page sizes and ID sequences, default/negative offset,
offset beyond total -> `[]`, and unchanged limit bounds. Signed-in outsiders
still receive 404 and cannot page into another project's letters.

Run `dotnet test --filter "FullyQualifiedName~IngestionOperationsTests"`.
**Common mistake:** applying Take before Skip or treating tie ordering as
a promise of snapshot pagination. **Done when:** stable-fixture pages work
and their concurrent-change limitation is documented. **Explain:** what can
happen to offset 2 if someone removes the first record between requests?

---

<a id="us-23"></a>
## US-23 — Filter dead letters by an error phrase

**User story:** As a project member investigating an incident, I want to
find failures containing `validation` so I can group related error cases.

**Current starting point:** inspection lists failure records but cannot
filter Error text. **Proposed scope:** optional literal, case-sensitive
`errorContains`, bounded to 100 trimmed characters. No payload search and
no changes to failure classification or retries.

**Open:** [IngestionEndpoints.cs](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[CaptureContracts.cs](../src/Pulse.Api/Contracts/CaptureContracts.cs), and
[IngestionOperationsTests.cs](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs).

**Target example:** errors `Payload failed validation`, `Storage unavailable`,
and `Other validation issue`; searching `validation` returns the first and third.

### Implementation steps

1. Seed those three dead letters in a fresh project with deliberate FailedAt
   values. Give the nonmatching record the newest time to expose paging errors.
2. Add an HTTP test expecting the two matching IDs, and a limit-1 test that
   must still return one matching record rather than an empty page.
3. Add nullable `errorContains` to the existing list handler. Keep authorization
   before this new validation and the ProjectId condition in every path.
4. Trim input, treat blank as omitted, and return 400 with `errors.errorContains`
   for a trimmed value longer than 100.
5. Add an Error.Contains condition to the query before ordering, limiting,
   and projection. Preserve offset support from US-22 if already present.
6. Preserve the existing response fields and original error text. Reading
   a filtered list must not acknowledge, delete, or replay any record.
7. Add case, no-match, literal `%`, excessive-length, and cross-project tests.
   Assert the original total stored letter count still exists after the GETs.
8. Document the error-field-only filter and case policy.

### Acceptance and verification

`validation` matches the two planned errors; `Validation` does not for that
case-sensitive fixture. A `%` term finds only errors actually containing `%`.
An unknown phrase yields `[]`. Blank is unfiltered, overlong input is 400,
and limit/offset, when present, apply to the matching set.

Run `dotnet test --filter "FullyQualifiedName~IngestionOperationsTests"`.
Use directly seeded error text rather than forcing a real service outage.

**Common mistake:** searching PayloadJson because it is the larger text
field, or building a SQL LIKE string that gives `%` wildcard meaning.
**Done when:** error-only matching, read-only behavior, and scope/paging are
verified. **Explain:** why is a harmless seeded failure record sufficient
to test this endpoint's filter?

---

<a id="us-24"></a>
## US-24 — Fetch one dead letter by ID

**User story:** As a project member, I want to open one known dead letter
directly so I can inspect it without searching successive list pages.

**Current starting point:** letters can be listed and replayed; there is
no detail GET. **Proposed scope:** one read-only route using the existing
DeadLetterResponse. No payload editing and no replay side effect.

**Open:** [IngestionEndpoints.cs](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs),
[CaptureContracts.cs](../src/Pulse.Api/Contracts/CaptureContracts.cs),
[ProjectAccessService.cs](../src/Pulse.Api/Auth/ProjectAccessService.cs), and
[IngestionOperationsTests.cs](../tests/Pulse.Tests/Api/IngestionOperationsTests.cs).

**Target contract:** `GET /api/projects/{projectId}/ingestion/dead-letters/{letterId}`
returns 200 with `{ id, payloadJson, error, attempts, failedAt }`, or 404 when
that letter is unavailable in the requested project.

### Implementation steps

1. Seed one letter with known payload, error, attempts, and failure time in
   a fresh project. Write a detail-GET test asserting all five response fields.
2. Add the GET route beside list/replay, using `:guid` constraints for both
   projectId and letterId. Do not change the existing POST replay route.
3. Use the neighboring handler's HTTP context, database context, access
   service, and cancellation-token parameters.
4. Call `RequireMemberAsync` first and return a denial immediately.
5. Query DeadLetterEvents using both `ProjectId == projectId` and `Id == letterId`.
   `AsNoTracking` is appropriate for this read-only lookup.
6. Return NotFound if the scoped lookup has no result; otherwise map the
   same five fields used by the list to DeadLetterResponse.
7. Test a nonexistent ID, anonymous/key-only callers, and a signed-in
   non-member. Also test an owner of A and B requesting B's letter under A.
8. Fetch the same valid letter twice and assert it remains stored with no
   new queue rows. Add route/status/credential details to the API reference.

### Acceptance and verification

The legitimate member can read every field accurately. Unknown or wrong-
project ID returns 404; unauthenticated or project-key-only access returns
401. Owning both projects does not make a child from B belong to A's route.
Repeated GETs neither consume the letter nor modify Attempts or FailedAt.

Run `dotnet test --filter "FullyQualifiedName~IngestionOperationsTests"`.
**Common mistake:** looking up by letter ID alone after authorizing the
project. **Done when:** response accuracy, all denial cases, and no-write
behavior are verified. **Explain:** why are authorization and scoped lookup
both required even for a read-only detail endpoint?

---

<a id="us-25"></a>
## US-25 — Reject reversed date ranges in synchronous event export

**User story:** As a project member exporting events, I want a clear error
when the start timestamp is after the end timestamp so I can fix the export
request instead of mistaking an empty file for missing activity.

**Current starting point:** synchronous event export accepts optional from/to;
the service independently applies inclusive timestamp conditions.
**Proposed scope:** validation in the existing GET `/export/events` handler,
for both JSON and CSV. Async export jobs and person exports are outside this task.

**Open:** [ExportEndpoints.cs](../src/Pulse.Api/Endpoints/ExportEndpoints.cs),
[ExportService.cs](../src/Pulse.Infrastructure/Services/ExportService.cs),
[ExportTests.cs](../tests/Pulse.Tests/Api/ExportTests.cs), and
[InsightEndpoints.cs](../src/Pulse.Api/Endpoints/InsightEndpoints.cs) for an
existing timestamp-range validation example.

**Target example:** `from=2026-09-09T00:00:00Z&to=2026-09-08T00:00:00Z`
returns 400 with `errors.from`, for either `format=json` or `format=csv`.

### Implementation steps

1. Add a focused HTTP test for each format using an authenticated project
   member and a reversed pair of valid timestamps. Expect a validation problem.
2. Find the synchronous events GET handler at the start of ExportEndpoints.
   Leave the later async-job and person-export mappings unchanged.
3. Keep membership and existing format validation in place. Add the new
   range check after those checks and before calling `EventsPageAsync`.
4. Compare only when both nullable timestamps are supplied. Reject from
   greater than to, and return a field error explaining the valid ordering.
5. Leave the service's inclusive lower/upper filters untouched. Equal
   instants must remain a valid range, even if expressed with different offsets.
6. Add an event at a fixed timestamp through capture, drain, and export a
   range with equal bounds at that instant. Assert that the event appears.
7. Add lower-only, upper-only, and omitted-bounds cases. Retain existing
   cursor-pagination tests and format behavior.
8. Update only the synchronous event-export contract in the API reference,
   including that invalid ranges return problem JSON even for CSV requests.

### Acceptance and verification

Reversed valid timestamps -> 400 in both formats; equal instants -> valid
export; one bound or neither -> existing behavior. For offset equivalence,
use `10:00Z` and `12:00+02:00` and URL-encode the latter's plus sign. Invalid
timestamp text still produces a binding error rather than a successful export.

Run `dotnet test --filter "FullyQualifiedName~ExportTests"`.
Assert error media type `application/problem+json` for the CSV failure case,
and retain existing successful CSV/JSON and cursor tests.

**Common mistake:** changing the service to an exclusive upper bound while
trying to add validation. **Done when:** reversed bounds are explained and
valid existing ranges/pagination still work. **Explain:** why should an
invalid CSV request return a structured error instead of an empty CSV file?

---

## Finishing one story before choosing the next

Copy this short record into your own notes or a PR description:

```text
Story ID:
User benefit, in my own words:
Files I changed:
Input/example I predicted before editing:
What I changed and why:
Focused check and actual result:
Boundary or access-denial case I verified:
Documentation I updated:
Checks I could not complete, with their actual error:
The common mistake I checked for:
My answer to the story's explanation question:
```

Prefer a small, understood change over completing several stories without
being able to explain their behavior. The repeated filter stories are
intentional: practice the same order until you can say and locate it in code:
**authorize -> scope the query -> filter -> order -> page -> return.**

For a second attempt, try a related story with the steps hidden, then compare
your plan before editing. Ask for one hint at the step you cannot explain.
These stories remain proposed until you implement and verify them yourself.
