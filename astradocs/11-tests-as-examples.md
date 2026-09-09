# Tests are examples you can ask the computer to check

**Keep one sentence:** a test states a concrete expectation and checks it
against running code.

## Read a tiny existing test

From [TimeBucketTests](../tests/Pulse.Tests/Domain/TimeBucketTests.cs):

```csharp
var ts = new DateTimeOffset(2026, 5, 10, 14, 37, 59, TimeSpan.Zero);
var bucket = TimeBucket.Truncate(ts, TrendInterval.Hour);
Assert.Equal(new DateTimeOffset(2026, 5, 10, 14, 0, 0, TimeSpan.Zero), bucket);
```

Read these as three sentences:

1. **Arrange:** choose `14:37:59 UTC` as the input.
2. **Act:** ask the code for the containing hour's start.
3. **Assert:** compare the actual answer with `14:00:00 UTC`.

`Assert.Equal(expected, actual)` compares what you expected with what the
code returned. A test that executes code without meaningful assertions may
miss a wrong answer.

## Three kinds of example in this repository

| Kind | Uses | Why choose it? |
| --- | --- | --- |
| Domain test | Pure rule or calculation | Explain one behavior with few moving parts |
| Infrastructure test | Real SQLite and services | Verify SQL behavior and transaction rollback |
| API integration test | HTTP host, services, database, workers | Verify the pieces together, including permissions |

[PulseApiFactory](../tests/Pulse.Tests/PulseApiFactory.cs) creates a private
named in-memory SQLite database for its test host. It keeps a connection
open so that database survives while the factory is in use. Tests do not
need your local `pulse.db` to exercise the API.

## Why async tests wait

Capture returns accepted before it promises processing success. Tests that
query afterward use [TestIngestion](../tests/Pulse.Tests/TestIngestion.cs)
to wait for the queue to drain. Then they assert the actual event or result.

Say it another way: waiting lets work settle; asserting checks whether the
settled result is right. Waiting alone does not prove success.

## Choose one example to investigate

```powershell
dotnet test --filter "FullyQualifiedName~TimeBucketTests"
dotnet test --filter "FullyQualifiedName~IngestionOperationsTests"
```

Run one command at a time. Read the name of the first failing test and its
message. A database timeout and an assembly blocked by Windows are different
failures. The [validation record](../docs/learning/validation.md) describes
known limits from the previous implementation session; these guides do not
declare those issues fixed.

**Check:** why would a mock database that always succeeds be a poor way to
prove rollback after a real SQLite write fails?

<details>
<summary>Answer</summary>

It would assume away the behavior being tested. The transaction tests use
real SQLite failures and inspect stored state after the exception.

</details>

Next: [make one small change](12-first-change.md).
