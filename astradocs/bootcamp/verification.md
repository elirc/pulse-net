# Verification record

This record distinguishes observed results from work still awaiting a check.
All entries below were recorded on 2026-09-08 against this working checkout.
The story ledger links features to their current evidence; a later code change
may require rerunning the relevant checks.

Final result: **656 passed, zero failed, zero skipped in each of two consecutive
full runs**, taking 7 minutes 57 seconds and 8 minutes 2 seconds. Both executed the
same final implementation and test binaries. All 75 stories now have recorded
acceptance evidence. Earlier failed runs remain here as part of the learning record.

| Check | Observed result | Scope and limitation |
| --- | --- | --- |
| Junior API integration class | 18 passed | New validation/filtering/count/detail contracts |
| Earlier complete suite | 326 passed, 1 failed | 1,000-event drain timed out; retained as historical evidence |
| Earlier isolated 1,000-event case | Failed | Last metrics: 228 pending, zero dead letters, 600 batch-reported processed; about 1.6 million captured log characters |
| Demo script parsing | Passed | Syntax only |
| Real local demo API | Defaults, count 1 with `checkout & pay`, count 5 with `signup` passed | Expected event/person counts and follow-up routes |
| Invalid demo arguments | Four checks passed before HTTP | Blank/201-character names and counts 0/11 |
| Headless Edge walkthrough | 12 behavior checks passed | Quiz state, reset/focus, scenario changes, glossary persistence |
| First upgrade class run | 6 passed, 1 failed | Exposed stale EF transaction enlistment after rejected adoption |
| Focused dashboard composition | 14 passed | Copy, batch layouts, selected refresh and request-local reuse |
| Integrated suite after the cleanup fix | **386 passed, 0 failed** | Includes all seven upgrade checks and new dashboard/template/insight classes; predates MID-08/MID-09 build |
| 1,000-event case within integrated run | Passed in 18.72 seconds | Original 60-second drain limit and 1,000-row assertion retained |
| Bootcamp consistency checker | Passed | All 75 story IDs and local lesson links; not feature correctness |
| Focused analytics composition | **9 passed, 0 failed** | Comparison math, boundaries/isolation, hour/day/week equivalence, read-key boundary |
| Roles and person activity integrated run | **417 passed, 1 failed** | All 418 executed; sole failure was the one-second rate-window recovery test, which expected 429 and observed 401 |
| New classes within that run | Passed | 8 database upgrades, 4 role, 2 competing membership, 10 person activity, 15 dashboard composition, 16 template, and 2 copy/import rollback cases |
| Offline upgrade rehearsal | Passed | Copied legacy adoption/upgrade/repeat, preserved data/admin backfill, unchanged original, missing/fresh handling, healthy API startup on upgraded copy |
| Remaining midlevel focused run | **54 passed, 3 failed** | Two nullable-double theory values had integer literals; export terminal-state fixture shared a database with a running worker; corrections await rerun |
| Revised rate-window recovery | Passed in focused run | Missing-key requests with a five-second window; still a wall-clock check |
| Flag governance application build | Passed, two existing nullable-Guid warnings | SR-03–SR-06 source compiled; migration/backfill and acceptance execution are separate pending gates |
| Governance focused acceptance run | **80 passed, 1 failed** | Stale tracked schedule after cancel/reschedule in one context; no-tracking replacement fix added afterward and awaits rerun |
| Corrected midlevel cases in governance run | Passed | Export lifecycle and discovery budget classes now pass, including the three formerly failing cases |
| Ingestion reliability focused run | **108 passed** | 3 minutes 34 seconds; migration, API, deduplication, receipts, capacity, retry/lease tests, and corrected scheduling regressions; `tmp/ingestion-reliability.trx` |
| Recoverable exports and snapshots | **57 passed** | 1 minute 49 seconds; active query cancellation, shutdown/recovery, stale publication, snapshot caps/mutation/reuse, API/migration/export regressions; `tmp/export-reliability.trx` |
| Event retention | **44 passed** | 1 minute 34 seconds; boundaries, restart, policy changes, transaction rollback, projections, API roles, migration/query regressions; `tmp/retention.trx` |
| Restricted personal tokens | **90 passed** | 4 minutes 8 seconds; route matrix, scopes, demotion/removal, redaction, expiry, revocation, JWT management, migration, legacy auth, and additional ingestion tests; `tmp/scoped-tokens.trx` |
| Erasure/tracing application build | Passed, two existing QueryService warnings | Shared compilation disabled; 4 minutes 20 seconds. Does not establish behavioral correctness |
| Initial erasure test build | Stopped before test execution | Shared build infrastructure stalled; no pass/fail count reported |
| Initial final-feature run | **88 passed, 1 failed** | Erasure initiation rollback expected raw SQLite exception but EF wraps it; assertion corrected. Trace, migration, erasure/read serialization, query budgets, sessions, alerts, and token regressions passed; `tmp/final-features.trx` |
| Expanded final-feature run | **91 passed, 0 failed** | 3 minutes 6 seconds; corrected erasure rollback, fenced-transaction expiry, post-erasure lease/replay, tracing, limits, sessions, alerts, and token regressions; `tmp/final-features-expanded.trx`. Subsequent strengthened alert concurrency/post-commit tests and exact session-input boundary await the full run |
| Final-schema upgrade and key restoration rehearsal | Passed | All 10 migrations; copied legacy adoption, repeated upgrade, preserved data and backfills, unchanged original, fresh/missing handling, healthy startup, refusal with a missing required suppression key, healthy startup after restoring that key; `tmp/upgrade-rehearsal-3f15c49d2d924c94af767c40a064c16a/evidence.json` |
| First final-suite build attempt | Failed before tests | New alert post-commit interceptor returned ValueTask where the EF override requires Task. Corrected; no tests counted from this attempt |
| Repeated invalid demo arguments | **4 passed** | Blank/201-character names and counts 0/11 still reject before HTTP |
| Repeated browser behavior check | **12 assertions passed; harness exit 13** | Unsettled Browser.close await prevented clean harness completion; reusable replacement is being verified separately |
| Repeated live local demo | **3 cases passed, exit 0** | Default two events, one `checkout & pay` event, five `signup` events; each produced one person. Fresh private database; own API stopped afterward. `tmp/final-demo-650bb97826fa41ceaa2b83799ac43cc1/evidence.json` |
| Reusable browser verifier | **12 checks passed, exit 0** | `node scripts/verify-walkthrough.mjs`; final invocation from `tmp` also passed. Own temporary profile, graceful shutdown with bounded recovery, verified path before cleanup; about 34 seconds including profile-handle release |
| First full regression after final features | **653 passed, 1 failed; 0 skipped** | 10 minutes 59 seconds, 654 executed. Sole failure: old blank-name validation test expected `distinct_id` text. Assertion now verifies `invalid_envelope`, one attempt, queue removal, and no stored event. Final empty-window/validation/preservation alert cases were added after this run's build. `tmp/bootcamp-final-full-1.trx` |
| First clean final full suite | **656 passed, 0 failed, 0 skipped** | 7 minutes 57 seconds. Includes all final test changes and the corrected ingestion assertion; `tmp/bootcamp-final-full-2.trx` |
| Unchanged final full-suite repeat | **656 passed, 0 failed, 0 skipped** | 8 minutes 2 seconds; same compiled implementation/tests, run with `--no-build --no-restore`; `tmp/bootcamp-final-full-3.trx` |
| Final documentation audit | Passed | All 75 story IDs have implementation/lesson/evidence links; generated story map checked; 97 Markdown files have no broken local file links; `git diff --check` clean. This checks file targets, not every URL or section anchor |

The integrated test process used `DOTNET_PROCESSOR_COUNT=2`. The test host
suppressed successful EF database-command logs, retaining warnings/errors.
Three managed-stack snapshots were taken during the long run. These conditions
must be disclosed when interpreting timing; this is not an isolated throughput
benchmark. The subsequent 418-test run also used two processors and diagnostic
sampling; it completed in 28 minutes 56 seconds with one rate-window failure.
The two final clean full runs satisfy the repository's repeat gate. They used
two processors and no managed-stack sampling. This is integration evidence on
this machine, not an isolated throughput benchmark or a deployment certification.

The run also passed the real SQLite transaction rollback and controlled
competing-write checks. No regular developer database was adopted or upgraded
as part of those tests; they use disposable files or private in-memory databases.

## Reproduce current checks

With the repository's .NET SDK available on PATH:

```powershell
dotnet restore
dotnet test --filter FullyQualifiedName~DatabaseUpgradeTests
dotnet test --filter FullyQualifiedName~DashboardCompositionTests
dotnet test --filter FullyQualifiedName~DashboardTemplateTests
dotnet test --filter FullyQualifiedName~InsightEditingTests
dotnet test --filter FullyQualifiedName~InsightUsageConcurrencyTests
dotnet test --filter FullyQualifiedName~AnalyticsCompositionTests
dotnet test --filter FullyQualifiedName~ProjectRoleTests
dotnet test --filter FullyQualifiedName~ProjectMembershipConcurrencyTests
dotnet test --filter FullyQualifiedName~PersonActivityTests
node scripts/check-bootcamp.mjs
node scripts/render-story-map.mjs --check
node scripts/verify-walkthrough.mjs
python scripts/verify-upgrade-rehearsal.py
```

The browser check requires Node with built-in WebSocket support and Chromium/Edge;
set `PULSE_BROWSER_PATH` if the verifier does not find Edge. The upgrade rehearsal
requires Python and a previously built Debug API. It creates private database
copies under `tmp` and stops the API processes it starts.

To reproduce the final full-suite conditions in PowerShell after restore:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet test tests/Pulse.Tests --no-restore -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=full-1.trx' --results-directory tmp
dotnet test tests/Pulse.Tests --no-build --no-restore --logger 'trx;LogFileName=full-2.trx' --results-directory tmp
```

These consecutive full runs implement the repository's policy after test changes.
Do not edit source between them; rebuild and repeat if a correction is needed.
Do not label a failed run as passed because a later focused test passed.
Do not label zero discovered tests as feature verification. Keep the journal's
observations, hypotheses, fixes, and subsequent evidence separate.
