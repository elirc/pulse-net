# A repeatable path from following code to owning a feature

You do not need to understand this repository in one pass. Use the same feature
several times, with a different question each time. The goal is to predict what
the code will do, explain why, and prove a change is safe. Reading more pages is
useful only when it helps you do those things.

Start at the [lesson index](README.md). Keep a copy of the [practice journal](practice-journal-template.md)
for your own observations. The [implementation journal](journal.md) records what
changed in this checkout; your journal records what changed in your understanding.

If the repository is new to you, begin with Lessons 1 and 2. They introduce the
request path and scoped data without requiring you to understand worker leases,
migrations, or recovery first. Follow the six blocks below in order through all
25 lessons; return to an earlier lesson whenever a later one exposes a weak spot.

## Four separate learning actions

**Reading** asks, “What behavior is promised?” Write the answer without method
names. Reading gives you a map; it is not execution evidence.

**Predicting** asks, “What exactly should happen in this example?” Name a status
or return value, rows that change, rows that remain, and the failure boundary.
Write this before running the example so the result can correct your model.

**Reproducing** asks, “Can I make the documented result happen?” Record the exact
focused command or request, the disposable fixture used, the observed result,
and the assertion that matters. A result from another machine is useful context,
but it is not your reproduced observation.

**Explaining** asks, “Why did this result survive the interesting failure?” Give
one ordinary-language explanation and one code-level explanation naming the
transaction, conditional update, fence, or validation boundary. Then change one
condition and predict again.

## Three passes through every lesson

**Pass one: read, then predict.** Read the example and response contract. Retell
it without class names: “The caller asks to export events; the server accepts a
job; a worker produces a document later.” Before inspecting the implementation,
write the expected result and one failure. This keeps the lesson's answer separate
from your own model.

**Pass two: follow the values.** Open the named endpoint, service, entity, and test.
Track one project ID and one child ID. Write where the request becomes validated
input, where SQL runs, and where a result becomes an HTTP response. For a worker,
also mark the boundary where the original request ends. Read a small method twice
instead of reading every file once.

**Pass three: reproduce, then explain.** Ask what happens if two callers act,
storage fails, the process restarts, the clock advances, or the user loses a role.
Choose one scenario, record a prediction, and run its focused test in a disposable
database. Record the observation before explaining it. Name the exact assertion
that catches the bug and the boundary that makes it pass. This pass turns a
walkthrough into engineering judgment.

## Choose your next practice block

| Block | Lessons | Skill to demonstrate before moving on |
| --- | --- | --- |
| Request basics | 1–2 | Explain validation, project scope, paging, and a response using one concrete request |
| Safe changes | 3–8 | Locate transaction boundaries, preserve old data, and distinguish authentication from role permission |
| Product behavior | 9–15 | Define identity, previews, copies, conditional edits, and current versus historical configuration |
| Durable work | 16–18 | Explain admission identity, retries, leases, cancellation, and fixed snapshot input after a crash |
| Data lifecycle | 19–21 | Reason about irreversible deletion, maintenance generations, key lifetime, and incomplete cleanup |
| Operational features | 22–25 | Trace execution, enforce complete-result budgets, derive sessions, and prove alert delivery once |

The numbers give dependencies, not a deadline. Revisit a previous block whenever
a later example reveals a gap. Learning the same transaction principle in cohort
replacement, export publication, erasure, and alert delivery is deliberate repetition.

## One worked study session

Use Session 17's durable retry. Before opening code, write this prediction:
“A SQLite contention failure records one failed attempt and a future due time;
a restart before that due time does not process the row.”

Find `IngestionRetryPolicy.AfterTransientFailure`. Calculate delays for attempts
one through five. Then follow `IngestionProcessor.ProcessLeasedRowCoreAsync` to
the transaction that stores Attempts and NextAttemptAt. Point to the lease fence.
Finally, find the test that advances the controlled clock to one tick before the
due time and then to exact equality.

After running it, write the observed result. Explain why `Task.Delay` inside one
worker would not preserve the same guarantee after restart. The next day, apply
that explanation to an alert rule's NextWindowStart. You are transferring a
concept rather than memorizing a method name.

## Practice changes in a separate branch or disposable copy

Repository test fixtures use private temporary databases. Prefer them for a first
reproduction. For a lesson that needs the running API, configure a new local
database created only for practice and verify the active connection string before
you send requests. Do not reuse a team, staging, production, or personally valuable
database. Keep migration and restore rehearsals on disposable copies as described
by the lesson; deleting a branch does not undo database mutations.

1. Pick a boundary already explained in a lesson, such as session gap equality.
2. Predict which test will fail if you change `>=` to `>`.
3. Make that one change and run the focused test against its private database.
4. Read the failure, identify the exact contract it violated, and restore the change.
5. Write the smallest legitimate feature request that would justify changing the
   contract, including how existing callers would learn about it.

Do not practice erasure or retention on your only data copy. Use the repository's
isolated fixtures and documented disposable rehearsal. A rollback of source code
does not restore data a feature deliberately deleted.

## Use hints in increasing depth

When stuck, first reread the input and expected output. Next, find the test name.
Then inspect the assertion and follow its service call. Only after that read the
full implementation. Record which hint helped. Over time, needing fewer hints
for the same kind of problem is stronger evidence of progress than finishing a
larger number of pages.

When asking for help, bring four things: the request, your predicted response,
the actual response or assertion, and the last code location you understand.
“Why does this request return 409 after a second edit?” is easier to investigate
than “I do not understand concurrency.” You can ask the larger question afterward.

## Assess understanding with evidence

| Level of ownership | Evidence you can produce |
| --- | --- |
| Follow with support | Trace a supplied request and explain a test with notes open |
| Make a bounded change | Define validation, edit the right layer, add a meaningful test, and explain the result |
| Own a feature | Write its contract, choose transaction boundaries, test races/failures, and document rollout/recovery |
| Review a system change | Identify interactions with other features, challenge hidden assumptions, and state what evidence remains missing |

These are practice milestones, not a promise of a job title after a fixed number
of hours. A useful review can include “I do not know yet; this experiment would
tell us.” The important distinction is between uncertainty you have named and a
guarantee you have assumed without evidence.

## Spaced review without rereading everything

After a lesson, answer three [review cards](review-cards.md) without looking at
their answers. The next day, draw the feature from memory and compare it to the
files. Several days later, explain the same principle using a different feature.
Return to a card you missed until you can give your own example and identify a
test that protects it.

Finish each practice entry with one sentence: “I used to think ___; the evidence
showed ___.” Keep the correction. Those corrections are the most valuable part
of this bootcamp.
