# Debugging labs

Run fault experiments in tests or a disposable local database. Start from a
clean committed change or a separate branch so you can inspect and undo your
own edits precisely. Do not run fault-injection SQL against useful data.

## Lab 1: accepted, but no event

**Symptom:** queue depth is zero and a trend count is zero.
**Setup:** use the poison-row setup from `IngestionPipelineTests`.
**Investigation:** capture response, queue state, dead-letter error, query
range. Which observation distinguishes failed processing from the wrong range?
**Deliverable:** a diagnosis with two ruled-out hypotheses and one supporting
test. Explain why waiting longer does not repair malformed data.

## Lab 2: retry creates a duplicate

**Setup:** in an isolated copy of the acknowledgement test, move queue
deletion after the event transaction commit and keep the rejecting trigger.
**Prediction:** write which database rows survive the first attempt.
**Investigation:** lift the trigger and run another attempt; compare counts.
**Deliverable:** restore the atomic implementation and explain the crash
window using a four-step timeline.

## Lab 3: the wrong project's letter

**Setup:** create two projects owned by the same test user; put a letter in B.
Call A's replay route with B's letter ID.
**Prediction:** why can membership checks alone fail to protect this case?
**Investigation:** inspect every query predicate before running the isolation test.
**Deliverable:** show a regression test that would fail if the project filter
were removed, and verify that B's letter remains untouched.

## Lab 4: the flaky age assertion

**Setup:** compare a test using actual elapsed wall time with
`Metrics_UseProjectScopeAndInjectedClock_AndEmptyQueueHasNoAge`.
**Investigation:** identify why thread scheduling changes one result but not
the other. Avoid adding a sleep to make the first appear stable.
**Deliverable:** use a fixed clock and assert empty, old, and future timestamps.

## Lab 5: export stuck after restart

**Setup:** in an isolated database test, insert an export job in `Running`
state, then create a fresh processor scope as if the process restarted.
**Investigation:** inspect the selection predicate in `ExportJobProcessor`.
**Deliverable:** a failing recovery specification and an ADR proposing leases
or explicit recovery. This is an unresolved exercise; the current processor
only selects `Pending` jobs.

## Lab 6: fast on small data, slow on larger data

**Setup:** choose a trend query and increase a reproducible dataset's size.
**Investigation:** inspect the query plan, rows materialized, and repeated
durations before changing code.
**Deliverable:** a measurement note with one hypothesis, one tested change,
and correctness checks. Report when the experiment disproves your hypothesis.

For every lab, finish with: "I predicted ..., observed ..., learned ...,
and would detect this in operation by ...". The diagnosis is part of the work.
