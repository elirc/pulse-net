# Tests that teach you how a system fails

**Outcome:** turn an unexpected result into a small reproduction, a reasoned
fix, and a regression test.

## Choose the boundary you need to prove

| Claim | Useful test boundary | Example |
| --- | --- | --- |
| A pure rule returns the expected value | Unit | TimeBucketTests |
| Database writes roll back together | Real SQLite, no HTTP host | IngestionTransactionTests |
| Another project cannot replay my letter | HTTP plus database | IngestionOperationsTests |
| A queued event eventually appears in a trend | HTTP plus running worker | IngestionPipelineTests |

Avoid mocking the behavior you are trying to establish. A fake database that
always reports success cannot establish database atomicity or SQL translation.

## Read a test as arrange, act, assert

In `AcknowledgementFailure_RollsBackEventPersonAndDefinitions_ThenRetrySucceedsOnce`:
arrange a queued event and a trigger; act by processing; assert that storage
and counters match the failure outcome. Then remove the trigger and verify
recovery. Both the failure and recovery state matter.

Before opening a test's implementation, write what bug its name should catch.
If its assertions cannot catch that bug, strengthen them or rename the test.

## Debug in a deliberate loop

1. Record expected and actual behavior with exact input and status.
2. Reduce the scenario: one project, one identity, one event if possible.
3. Locate the earliest point at which reality differs from your prediction.
4. State one hypothesis that a test or observation can disprove.
5. Change one thing; run the smallest relevant test.
6. Verify the failure path and nearby behavior; run the full required checks.

Do not edit five layers until something passes. That destroys the evidence
that would teach you which assumption was wrong.

## Time and concurrency

Use fixed timestamps for business assertions. Queue age tests inject a
`TimeProvider`; no waiting is needed to assert 90 seconds of age.
Integration tests wait for a condition through `TestIngestion`, with a
deadline. They do not assume a worker finished because 500 ms elapsed.

An empty queue is not itself a successful ingest: inspect dead letters or
the resulting event. Avoid exact global-counter assertions across unrelated
projects; counters are process-local and reset at startup.

## Commands

```powershell
dotnet test --filter "FullyQualifiedName~IngestionOperationsTests"
dotnet test --filter "FullyQualifiedName~IngestionTransactionTests"
dotnet test
```

Follow the repository's [testing policy](../testing.md), including its two
consecutive full-suite passes for test changes. Do not rerun a failure until
it happens to pass; first capture and explain it.

**Exercise:** remove the replay transaction in a disposable branch and run
the enqueue-failure test. Explain why a happy-path test would miss the bug.

**Done when:** your review includes the original failing behavior, which test
would fail without the fix, and the important behavior the test does not cover.
