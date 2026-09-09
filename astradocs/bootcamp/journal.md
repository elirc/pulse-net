# Implementation journal

## Entry 001 — Establish the scope and a reproducible starting point

The task is to implement the 25 junior, 30 midlevel, and 20 mid/senior stories,
while treating teaching material as the main deliverable. The checkout already
contains earlier ingestion work and onboarding documents; those changes are
preserved. The 75-story ledger begins with every story planned so status cannot
be mistaken for completion.

The shell initially could not find `dotnet`. Inspection found SDK 10.0.400 at
`C:/Users/E/.dotnet/dotnet.exe`. This is a command-discovery issue, not evidence
that the application fails to compile. Commands in this environment use that
explicit executable until PATH is configured in the invoking process.

The older validation record reports an application-control assembly block and
a large-batch timeout. A fresh focused ingestion run is being used to establish
current behavior before interpreting those historical observations as current.
No machine protection setting has been changed.

**Learning checkpoint:** distinguish an unavailable command, a compile error,
a host-startup failure, and a failed assertion. They occur at different layers;
changing application logic before identifying the layer often creates a second
problem without fixing the first.

## Entry 002 — First implementation and current verification

The six practice-tool stories now have code: demo name/count parameters and
follow-up routes, plus walkthrough reset, quiz visibility, and glossary.
PowerShell parsing passed. Runtime demo/browser evidence is still pending.

The 19 junior API stories now have code and a new integration test class.
Small shared InputRules keep normalization consistent while endpoints retain
their authorization, query, and mutation responsibilities. The key test fixtures
use exact timestamps, special text characters, foreign projects, and mixed-validity
updates so they can catch plausible-looking incorrect results.

The focused existing ingestion run passed all 18 tests. The earlier assembly
loading failure did not recur in that run. New junior-story checks and the
broader suite are still pending; that result does not certify all new features.

**Learning checkpoint:** "implemented" means code exists; "verified" means the
specific contract has supporting evidence. Keep these words separate in both
the journal and the story ledger.

The entries below record subsequent evidence; this entry preserves what was
known at the time instead of rewriting a pending check as an earlier success.

## Entry 003 — Observe behavior at three boundaries

The new junior API integration class passed all 18 cases. The broader suite
passed 326 tests and failed the existing 1,000-event ingestion drain test.
That failure also reproduced in isolation, so it is an unresolved suite gate.

The real demo API accepted the default two-event batch, a one-event batch named
`checkout & pay`, and five `signup` events. Each run reported the expected
event count and one person, and printed the three follow-up routes. These
examples exercise default values, URL encoding, and the single-element array
boundary. Script rejection cases still need their own evidence.

A headless Edge check passed 12 walkthrough behaviors, including quiz visibility
and accessibility state, clearing an answer, resetting the scenario and focus,
and preserving the glossary disclosure state. An API test cannot establish
these browser behaviors: verification belongs at the boundary being changed.

The failing ingestion run reported 228 pending rows, zero dead letters, and
600 processed events in its last metrics sample. Its captured output contained
about 1.6 million characters, mostly database command logs. The worker was
making progress. That rules out some explanations, but it does not yet prove
that logging causes the timeout. The batch-level metric is also not a precise
count of every commit at the instant of sampling. The next experiment should
separate output overhead from processing cost while preserving the 60-second
deadline and the assertion that all 1,000 events exist.

**Try explaining it twice:** a timeout means the observer stopped waiting; it
does not automatically mean the worker stopped working. In database terms,
inspect durable rows and failure state before interpreting a sampled counter.

## Entry 004 — Make upgrades explicit before adding tables

SR-01 is in progress. The new design separates inspecting a database, adopting
a recognized legacy schema, and applying migrations. Normal startup verifies
the schema. The test host uses an explicit test-only initializer for its private
database. This keeps a production startup from silently becoming maintenance.

A frozen fixture contains the 19-table legacy schema and its indexes, with no
application rows or credentials. Tests can populate that old structure, run
adoption and upgrades, and check preservation of IDs, keys, timestamps, queue
attempts, and JSON. The fixture must stay frozen when the current model changes.

**Current limitation:** migration scaffolding has reported a build failure;
diagnosis and upgrade verification are still pending. These files are work in
progress, not a verified migration release.

## Entry 005 — Separate build locks, validation, and feature evidence

The demo API was still holding an assembly that the build needed to replace.
The diagnostic identified its exact process. Stopping that owned demo host let
the build finish successfully. The baseline migration and model snapshot were
then generated. This is an operational file-lock failure, distinct from a C#
compiler error. Do not change source code to treat the wrong failure layer.

The demo's remaining input checks passed: blank and 201-character names, and
counts 0 and 11, all reject before an HTTP request. Together with the earlier
live examples this supplies the junior practice-script evidence.

MID-01, MID-03, and MID-04 now have implementation and tests awaiting execution.
Duplication owns new dashboard/tile rows while sharing insights. Batch layouts
validate the entire input before assigning any tracked entity. Selected refresh
resolves ownership before query execution and reuses results only within that
request. The new lesson explains the same rules with examples, a reference
diagram, a bookmark analogy, code traces, and teach-back exercises.

The upgrade tests now seed projects, people, events, flags, queued work, and a
completed export. The inspector also compares tokenized table definitions:
column PRAGMAs alone do not describe every CHECK constraint or collation. This
is why "we checked the columns" is weaker than "we checked the supported schema."

One controlled ingestion experiment changes only collection of successful EF
SQL command logs in the test host: warnings and errors remain enabled. The
1,000-event timeout and event-count assertion stay unchanged. Results are still
pending, and concurrent machine load can also affect elapsed time. A passing
run alone will not prove logging was the sole cause.

## Entry 006 — Failure-path evidence changes the implementation

The first database-upgrade run passed six of seven tests. Rejected legacy
adoption correctly avoided recording a baseline, but a later inspection through
the same DbContext failed: EF retained an enlistment wrapper for a disposed
SQLite transaction. The fix makes the EF enlistment itself asynchronously
disposable, so exceptional exits clear both layers. Rerun evidence is pending.

This is a useful distinction to teach: rollback of database changes and cleanup
of in-memory transaction state are separate responsibilities. A test that only
asserts "an exception happened" would miss the second problem. The existing
rejection test continued by inspecting the database and exposed it.

All 14 dashboard-composition integration cases passed. Further rollback and
compatibility checks remain separate gates. The bootcamp link checker also
passed with all 75 IDs present; that result checks documentation consistency,
not application behavior.

Preview, replacement, usage reporting, and guarded insight deletion now have
code and tests in progress. The first build caught a missing required entity
Name on the untracked preview object; assigning an internal Preview label fixes
the compile-time contract without requiring a name in the HTTP preview request.
The new lesson separates validation, normalization, and resolution, and traces
why a saved recipe differs from a completed export document.

## Entry 007 — First integrated pass and measured limits of the evidence

The integrated run passed all 386 tests in 12 minutes 57 seconds. It includes
seven database-upgrade checks, 14 dashboard-composition checks, the copy
rollback test, 12 template checks, 17 config-validator checks, seven insight
editing checks, and the controlled add-tile/delete concurrency test, alongside
the existing suite. The rejected-adoption cleanup fix now passes.

The unchanged 1,000-event acceptance test passed in 18.72 seconds. Its drain
deadline is still 60 seconds and it still asserts 1,000 persisted events. The
test host filtered successful EF command logs while retaining warnings/errors.
This is a useful improvement over the earlier timeout, but not an isolated
benchmark proving one sole cause: processor-count settings, concurrent machine
load, and diagnostic sampling also differ. A clean subsequent run remains a gate.

The long quiet run prompted managed-stack snapshots. The first showed host
startup/disposal, another showed request logging, and another showed password
hashing and a junior API test. Their changing positions and increasing CPU time
supported progress, not a fixed hang. A single waiting stack would have been
insufficient evidence to rewrite teardown code or weaken a test deadline.
The snapshots used the documented
[dotnet-stack report command](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-stack),
installed only under the ignored workspace diagnostics directory.

MID-08 and MID-09 were added after that integrated build. Their code and tests
therefore do not inherit its pass: a new focused run is in progress. The new
lesson contrasts half-open comparison windows with inherited inclusive trends,
explains null percentage changes from zero, and demonstrates why unique-person
counts cannot be summed across event series.

The role/scope matrix is drafted before enforcement. Roles remain pending;
the table is explicitly labeled as design. Schema transition evidence from the
next migration, credential redaction, and competing last-admin changes are the
next management foundations.

## Entry 008 — Permission boundaries and the first schema evolution

The nine analytics composition checks passed in their focused run. The next
implementation adds viewer/editor/admin memberships, an explicit route matrix,
and omission of project keys from non-admin responses. Existing memberships
are backfilled to admin; new invitations explicitly start as viewer. Those
different defaults preserve existing access while making new access deliberate.

Membership changes acquire a write gate inside the service transaction and
then recheck the actor and remaining administrator count. The new competing
mutation test coordinates two separate contexts before transaction acquisition;
it does not use random sleeps to hope for a race. Execution is pending in the
full roles/activity run, so the ledger does not claim this invariant passed yet.

The migration preservation fixture now inserts frozen legacy columns with
parameterized SQL. Using today's EF mappings to populate yesterday's database
would fail as soon as a new entity property appeared. This is a test-design
lesson: setup must represent the historical contract you intend to verify.

MID-10/MID-11 add canonical-person timelines and activity summaries. The
timeline binds a versioned position to its project/person/filter and checks
timestamp ties against real SQLite. The summary counts processed event rows,
uses UTC dates and an exclusive end boundary, and documents its timestamp-only
memory cost. Tests include an actual identity merge. Build/execution remain
pending; the corresponding lessons distinguish implementation from evidence.

## Entry 009 — A passing feature check can coexist with a failed suite

The roles/activity run completed all 418 tests: 417 passed and one failed. New
role guards, concurrent last-admin protection, legacy backfill, person timelines,
activity summaries, selected-refresh cancellation, unsupported template exports,
and import rollback passed. The failing test expected three database-backed
capture requests to fit inside a one-second limiter window; its third response
was 401 rather than 429 during a 32-second test. That is consistent with a reset
between requests, not proof of a broken admission counter.

The rate-window check now uses missing-key requests, which are admitted then
rejected before database lookup, and a five-second window with matching reset
wait. Its assertions still require two admissions, a rejected third request,
and recovery after reset. This reduces unrelated work and scheduling sensitivity;
it remains a wall-clock integration test rather than a deterministic fake-clock
proof. Its rerun is pending, and the prior failure is retained in the record.

A managed-stack snapshot during the long run showed authentication and test
continuations executing. It supported ongoing work rather than a fixed hang.
One earlier redirected diagnostic attempt produced an empty artifact and was
not used as evidence. The cancellation probe also gained a finally block that
cancels even when an entry assertion times out; disposing its token source
alone would not release a waiting request.

The separate offline rehearsal passed adoption and upgrade on a restored copy,
preserved the original legacy database, verified the admin backfill, upgraded
a fresh file, and started a healthy API against the upgraded copy. No regular
developer database was modified.

Implementations and tests for the remaining midlevel stories were added after
the roles/activity build. They do not inherit its passing results. A focused
run is building them now. Sessions 10–14 explain audience lifecycles, flag
diagnostics and read reuse, admission versus processing, export byte identity,
and honest bounded observations, with links to each feature's tests.

## Entry 010 — Isolate the experiment before blaming the feature

The remaining midlevel focused run executed 57 checks: 54 passed and three
failed. Two failures occurred before the status-policy method ran: xUnit's
reflection invocation could not convert integer literals to nullable doubles.
The cases now use `60.0` and `61.0`. This fixes the test inputs while preserving
the equality boundary they were intended to exercise.

The third failure expected deletion of a pending export to return 409 but
observed 204. The test removed the worker from a derived host, yet that host
shared the class fixture's SQLite database. The original host's worker remained
able to process the fixture job. A fresh parent factory now gives that test
its own database before constructing the worker-free host. The conditional
terminal-only deletion predicate remains unchanged. Rerun evidence is pending.

The revised rate-window recovery case passed in this run. That supports this
specific check; it does not make the full run successful. The verification
record retains all three failures and the earlier rate-window failure.

SR-03–SR-06 now share a central flag mutation service. Conditional SQL advances
the revision, and the surrounding transaction includes version history, audit,
and pruning. Restoration advances to a new revision; scheduling applies only
when its creator still has editor permission and its expected revision matches.
The scheduler's state joins the same transaction. Session 15 repeats these
ideas through a two-person example, a database conversation, a file map,
diagrams, a response table, and prediction exercises.

The governance application build passed with the two existing nullable-Guid
warnings in QueryService. The new migration backfills revision 1 and one
baseline snapshot per existing flag. It retains malformed or oversized legacy
configuration as string fields rather than parsing away historical data.
New writes enforce the configuration budget; restoration validates current
support. Baselines have no invented actor and no retrospective audit entry.
Focused migration, race, rollback, API, and corrected midlevel checks are now
building/running. Their presence in source is not recorded as a test pass.

## Entry 011 — Tracked objects are not a fresh database read

The governance run completed 81 checks: 80 passed and one failed. Audit
attribution/redaction, conditional edits, restoration, migration baselines,
history retention, rollback, and competing processors passed. The three earlier
midlevel failures also passed after their test corrections.

The remaining failure performed schedule creation, cancellation, and replacement
using one context. Cancellation used immediate SQL; the earlier tracked schedule
still said pending. A later entity query returned that tracked object, so
replacement incorrectly returned 409. The lookup now uses AsNoTracking and
detaches the prior tracked entity before replacing its unique flag schedule row.
This is the same underlying distinction taught with immediate bulk updates:
database state and the context's identity map are related, but not identical.
The correction was added after the run's build and awaits a rerun.

The next ingestion work connects five stories before testing their interactions.
Client retry keys, processing items, receipt positions, and queue sequences have
separate identities. Capture/replay/limit edits share a durable admission gate.
Worker attempts use stored due times and only recognized SQLite contention
receives backoff. Every worker mutation checks an owner/generation lease in its
transaction. New Sessions 16 and 17 explain those relationships with tables,
crash timelines, code maps, and prediction exercises.

The ingestion application build passed. A new nullable-reference warning at
the manually parsed capture body was fixed by an explicit null check; the two
older QueryService nullable-key warnings remain. Migration generation/backfill
and the ingestion acceptance run are still pending. Existing resilience tests
are being updated for the deliberate contract change: malformed properties are
permanent validation failures, and failed attempts are counted consistently.

## Entry 012 — Recovery must prove ownership and preserve input

The ingestion reliability run passed all 108 tests in 3 minutes 34 seconds.
This includes the corrected scheduler replacement, upgrade/backfill checks,
raw capture validation, retry deduplication, receipt transitions, capacity,
durable retry schedules, stale-worker rejection, and existing transaction
regressions. The final integrated suite is still pending; a focused pass does
not establish every interaction with later data-lifecycle features.

Exports now use a related ownership protocol. A current attempt must match
the job's owner, generation, Running state, and unexpired lease before publishing.
Cancellation persists before a worker observes it. A monitoring scope has its
own context so it can cancel an active query without concurrently using the
render context. Host shutdown leaves recoverable work rather than recording
a misleading Failed result.

The snapshot implementation copies complete event-export values and commits
readiness with the copy. Tests explicitly mutate source data after capture:
an actual identity merge, source deletion, property change, and late insertion.
Other tests cover empty snapshots, row/byte caps, save failure, and resumed
rendering in both JSON and CSV. These tests have been added and are building;
their results are not yet known.

The export application build passed with the two existing nullable-key warnings.
The migration initializes legacy jobs to live consistency, unowned generation
zero, and no snapshot marker. Existing Running jobs are therefore claimable;
completed documents and timestamps remain unchanged. Session 18 teaches the
difference between cursor position, a time cutoff, and fixed copied values,
and distinguishes same-job recovery from a new user-requested retry job.

## Entry 013 — A small transaction can carry a large guarantee

The export focused run completed successfully: 57 passed in 1 minute 49 seconds.
The active-query cancellation probe confirmed that the worker's linked token
actually interrupted the blocked query. Host shutdown left Running work that a
new owner recovered after fake-clock lease expiry. Both JSON and CSV recovery
rendered the exact expected bytes from the previously captured snapshot.

The snapshot cap cases rolled back without copied rows or a ready marker.
The source-mutation test used the real identity merge service and verified that
source PersonId changed while the stored snapshot still contained its original
value. This is stronger evidence than simply naming a test "snapshot is stable."
Three test-analyzer warnings about completed Task.Result accesses were corrected
to await the tasks; those source-only corrections will be compiled in the next run.

Retention now uses the project's durable gate to order policy edits against
batch decisions. A batch records a fixed cutoff/revision, selects only up to
1000 IDs, retires receipt pointers, deletes scoped eligible rows, and updates
progress in one transaction. Policies deploy disabled. The application build
passed, the migration includes disabled backfill, and the focused tests are
building. Session 19 and the retention runbook explain observations versus
promises, equality boundaries, restart progress, and what retention does not delete.

The next authorization work adds restricted personal tokens before the broad
erasure workflow. This ensures new administrative lifecycle routes have an
explicit token boundary as well as a project role. Erasure remains outstanding;
its review must include queued identities, copied exports, and in-flight data
operations, not just deleting the visible person row.

## Entry 014 — Restrictions compose; cleanup crosses boundaries

The retention run passed 44/44 checks. The restricted-token run passed 90/90,
including project allowance, scope, current role, expiry equality, revocation,
JWT-only token management, credential redaction, and migration preservation.
Four additional ingestion ownership/transaction checks also passed in that run.
Session 20 explains the authorization decision as the intersection of independent
restrictions, with examples of why a valid credential can still be denied.

Erasure now has a durable job and a project pause. Initiation freezes known
aliases as versioned HMAC fingerprints, invalidates ingestion lease authority,
and advances a maintenance generation. Cleanup visits exports, snapshot copies,
events, queue envelopes, dead letters, cohort links, aliases, and the person.
The old DELETE route deliberately changes to 202 acceptance and a status URL.
Session 21 repeats the design as a person journey, workshop analogy, state
machine, database inventory, and controlled failure exercises.

The cross-feature review identified an important race: a request could pass
authorization before erasure, wait until cleanup finishes, then save stale data.
The write context now remembers the observed maintenance generation and checks
it in the saving transaction. Bulk-only operations need an explicit transactional
check too. Read endpoints use a coherent SQLite read transaction so a pause
cannot commit halfway through the data view they assemble.

The first erasure application build passed. A subsequent test build spent a long
time waiting on shared build infrastructure and was stopped before producing a
test result. Only this repository's identified build processes were stopped.
Rebuilding with shared compilation disabled succeeded in 4 minutes 20 seconds,
including tracing source, with the two existing QueryService warnings. No erasure
or tracing test is recorded as passed on the strength of that build.

At the user's request, three Sol 5.6 subagents now handle bounded queries/sessions,
hourly alerts, and erasure review in separate files. Shared wiring, migrations,
tracing, the learning ledger, and integrated verification remain coordinated here.
Each implementation must leave a lesson and reproducible checks, not only code.

## Entry 015 — Test the interleaving, not only the method name

The first integrated final-feature run executed 89 tests: 88 passed and one
failed. The failure was an assertion about the exception type from an injected
initiation failure. EF's SaveChanges wraps the SQLite error in DbUpdateException.
The assertion now checks that wrapper and its SQLite error code while keeping
the pause/job/fingerprint rollback checks. A later run must execute those checks
before rollback is counted as verified.

Passing groups included tracing across five failed attempts and replay, nullable
trace migration preservation, erasure's read/pause serialization, scoped tokens,
real row/byte/annotation/session budgets, legacy trend equivalence, canonical
identity sessions, and initial alert management/evaluation tests.

The SQLite review changed the read gate from deferred to immediate transactions.
A WAL reader can keep an old snapshot while another writer commits, so a coherent
snapshot alone did not prove the promised pause ordering. Protected reads now
hold SQLite's reserved writer lock through result assembly. This also serializes
those reads with writers and one another; the lesson states that v1 tradeoff.
The controlled test waits for a writer's transaction-start attempt and releases
the reader before awaiting completion, avoiding a test-induced synchronous lock hang.

Two other review findings became concrete fixes. Raw SQL read-marker timestamps
must bind UTC ticks explicitly because entity property conversion does not apply
to an arbitrary interpolated parameter. Numeric enum inputs such as interval=0
must be rejected when the API promises named intervals. Session duration now
preserves fractional seconds instead of silently truncating them.

The expanded run adds fenced-transaction expiry and post-erasure lease/replay
checks. Alert tests now synchronize actual competing processors and edit timing,
rather than assuming Task.WhenAll guarantees overlap when SQLite work can complete
synchronously. A post-commit injected failure checks recovery from persisted
progress. Bounded query measurements warm both the new and legacy paths and
record allocations and duration with process-wide measurement caveats.

## Entry 016 — Evidence has a scope

The expanded final-feature run passed 91/91 tests in 3 minutes 6 seconds. The
corrected erasure initiation test now verifies the EF exception wrapper and the
unchanged database together. Lease expiry inside a fenced transaction, revoked
ownership after erasure, and suppression of a delayed replay also passed. This
does not turn every later test edit into a pass: stronger alert interleavings and
the exact 10,000-row session boundary still need the final full suite.

The offline rehearsal used copies and fresh private databases. All ten migrations
applied, repeated upgrade was harmless, the original legacy database stayed
unchanged, and old memberships and disabled retention defaults survived. A
required suppression key was deliberately made unavailable: startup refused to
serve. Restoring that rehearsal key allowed healthy startup again. A schema
upgrade and restoring the keys needed to interpret its data are separate checks.

The warmed trend comparison did **not** show a performance improvement on its
small fixtures. The bounded path used more allocated bytes and elapsed time at
10, 100, and 1,000 rows. Lesson 23 records the numbers and limitations rather than
calling the new path faster. Its demonstrated benefit is rejecting work beyond
explicit limits without returning a silently partial answer. Predictable limits,
throughput, and retained memory are different claims requiring different evidence.

The first final-suite compilation found a test interceptor signature mismatch:
EF expects Task for TransactionCommittedAsync. Correcting that override allowed
test discovery. A separate browser harness passed all twelve UI assertions but
left an unsettled await while closing the browser, producing exit code 13. The
harness needs a clean teardown before the whole command can be reported as passed.

**Try this:** choose one result above. Write the observation, the narrow claim it
supports, and one claim it does not support. Then find the relevant source and
test through the story ledger. This is how a code review turns a reassuring
sentence into evidence another engineer can inspect.

The full regression run subsequently found an older ingestion assertion that
looked for `distinct_id` in a dead-letter message even though its fixture supplied
that field and deliberately left the event name blank. The shared envelope
validator now records the stable `invalid_envelope` category. The revised test
checks that category, exactly one failed attempt, an empty queue for the project,
and no persisted event. Updating a message assertion is justified by the changed
contract; preserving the storage assertions keeps the behavioral test meaningful.

## Entry 017 — Freeze the implementation and repeat the experiment

The full run completed 654 tests in 10 minutes 59 seconds: 653 passed, one failed,
and none were skipped. The old validation-message assertion was the only failure.
It passed the correct malformed envelope into the current processor but compared
against an earlier diagnostic convention. The next build includes its stronger
storage checks and the final alert empty-window, named-validation, and preserved
history cases.

The walkthrough verifier now passes twelve behavior checks and exits zero,
including invocation from outside the repository root. Its cleanup resolves the
owned temporary profile before removal and only escalates termination while its
isolated browser control connection is still live. Three live demo variants and
four input-rejection cases also passed. The normal developer database was not
used for these experiments.

The implementation is now held stable while the two consecutive full-suite gates
run. A later source or test correction would require checking the resulting
version again. The generated story map makes the ledger readable; it is rebuilt
from the same data rather than maintaining a second hand-edited status list.

## Entry 018 — Complete the evidence trail, then practice owning the change

Both final full suites passed all 656 tests with zero failures and zero skipped
tests. The first took 7 minutes 57 seconds; the unchanged repeat took 8 minutes
2 seconds. The corrected ingestion assertion passed, as did all thirteen alert
infrastructure and five alert API cases. The repeat used the same compiled
implementation and test binaries. The ledger now marks all 75 stories verified
and the readable story map is regenerated from that evidence.

The delivery includes 25 core lessons, 64 review cards, a practice journal,
the implementation journal, and a final capstone review. The capstone follows a
lost capture response through worker recovery, export snapshots, erasure, and
analytical evidence. Each checkpoint asks for a prediction before revealing an
answer. It also exposes a useful trap: the bounded trend's end is inclusive,
while hourly alert windows are half-open. Similar time labels can hide different
contracts.

The final onboarding review removed stale claims that ordinary startup creates
the schema with EnsureCreated. New learners now build and explicitly upgrade a
disposable database before starting the API. Architecture and review findings
distinguish implemented recovery mechanisms from remaining limitations such as
retry overtaking, finite deduplication lifetime, legacy query allocations, and
serialized protected reads. The documentation audit checked 97 Markdown files
with no broken local file links; the browser verifier and live demo also passed.

**Your next practice loop:** start with Lesson 1, record a prediction in your
practice journal, reproduce one result, and explain the responsible boundary.
Move through the lessons at a pace that lets you repeat the explanation without
copying it. After that, use the capstone to connect several features in one
review. Completing this implementation supplies examples to study; independent
prediction, diagnosis, and explanation are the skills you are developing.
