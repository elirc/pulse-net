# A guided first contribution

**Goal:** add one useful test before attempting a feature across many files.
This page is an exercise; its proposed test is not already added for you.

## The task in one sentence

Prove that rounding `14:00:00 UTC` down to an hour keeps it at `14:00:00 UTC`.

The existing test checks a time partway through the hour. This checks the
exact boundary. Explain the difference before editing anything.

## Step 1: look at your starting state

```powershell
git status --short
git diff -- tests/Pulse.Tests/Domain/TimeBucketTests.cs
```

There may be existing work in the checkout. Identify it so your later diff
is understandable. Do not discard changes to make the status look tidy.

## Step 2: write the prediction

On paper or in a note:

```text
Input:  2026-05-10 14:00:00 UTC
Action: truncate to hour
Output: 2026-05-10 14:00:00 UTC
Why: the input already is the start of its hour
```

## Step 3: try the code with a hint

Open [TimeBucketTests](../tests/Pulse.Tests/Domain/TimeBucketTests.cs).
Add a `[Fact]` with arrange, act, and assert. Reuse the existing test's
structure, but use the exact boundary. Give the test a name describing
the behavior rather than naming it `Test2`.

<details>
<summary>Show a worked solution after trying</summary>

Add this method inside the existing `TimeBucketTests` class:

```csharp
[Fact]
public void Truncate_Hour_ExactBoundaryStaysUnchanged()
{
    var timestamp = new DateTimeOffset(2026, 5, 10, 14, 0, 0, TimeSpan.Zero);

    var actual = TimeBucket.Truncate(timestamp, TrendInterval.Hour);

    Assert.Equal(timestamp, actual);
}
```

No product-code change is expected for this exercise. The existing
implementation should already satisfy the invariant.

</details>

## Step 4: run it and understand a failure

```powershell
dotnet test --filter "FullyQualifiedName~Truncate_Hour_ExactBoundaryStaysUnchanged"
```

Expected: the new test is discovered and passes. If no tests match, check
the name and class placement. If compilation or assembly loading fails,
record that first; your assertion has not run yet.

As a temporary exercise, change the expected timestamp to `15:00`. Predict
and observe the assertion failure, then restore the correct assertion.
That deliberate wrong answer helps show what your test actually detects.

## Step 5: explain your diff

Write: "I added an exact-hour boundary example. It should keep an already
rounded timestamp unchanged. I ran ... and observed ... ."

Run relevant neighboring tests and follow the existing
[testing policy](../docs/testing.md) before treating a test change as ready
to merge. The known environment blocker may prevent that final verification.

**Repeat with less help:** add an exact-day boundary example yourself.
After you can explain both, choose a bounded task from the
[engineering exercise backlog](../docs/learning/exercise-backlog.md).
