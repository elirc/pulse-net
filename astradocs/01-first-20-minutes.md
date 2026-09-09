# Your first 20 minutes

**Today's goal:** explain what Pulse does and find one small piece of code.
Twenty minutes is a suggestion. It is fine to spread this page across sessions.

## First, say what this project is

Pulse is a backend that collects facts about activity in a product. It can
answer questions such as "How many people viewed the pricing page?" There
is no product frontend in this repository; you interact through HTTP requests.

An **event** is one recorded action. A **project** groups that product's data.
You already have enough vocabulary for the first tour.

## Open only three locations

1. [README.md](../README.md): recognize the project name and stack.
2. [src](../src): find `Pulse.Api`, `Pulse.Infrastructure`, and `Pulse.Domain`.
3. [TimeBucket.cs](../src/Pulse.Domain/TimeBucket.cs): find `Truncate`.

You are looking for landmarks. Skip code you cannot explain yet.

In `Truncate`, find the `TrendInterval.Hour` branch. It builds a timestamp
whose minutes and seconds are zero. For `14:37:59 UTC`, predict `14:00:00 UTC`.
This small calculation helps group events into chart buckets.

## Optional: run that one example

From a terminal in the repository root:

```powershell
dotnet --version
dotnet test --filter "FullyQualifiedName~Truncate_Hour_DropsMinutesAndSeconds"
```

`dotnet` is the development tool. `test` asks it to run tests. `--filter`
narrows the selection to the named example. The expected assertion checks
that the hour stays 14 while minutes and seconds become zero.

If `dotnet` is missing, see the existing
[SDK setup steps](../docs/learning/01-first-session.md). If Windows reports an
Application Control block, record it as an environment issue; see
[when stuck](13-when-stuck.md). You can continue the reading exercises.

## Say it another way

Product question: "What happened during each hour?"

Code question: "Which hour-start timestamp should label this event?"

Small answer: `14:37:59` belongs in the `14:00:00` bucket.

## Stop and check

Without scrolling up: what does an event represent? Which folder contains
`TimeBucket`? What did the test ask the code to prove?

<details>
<summary>Check your answers</summary>

An event records one action. `TimeBucket` lives in `Pulse.Domain`. The test
checks that rounding down to an hour removes minutes and seconds.

</details>

**Enough for today:** you found a real function and explained one result.
Next: [the big picture](02-big-picture.md).
