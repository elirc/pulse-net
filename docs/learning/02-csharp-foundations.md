# C# foundations through real code

**Prerequisite:** complete the first session. **Outcome:** read the service
methods without treating language syntax as magic.

## Values, nulls, and records

`Guid` identifies a project or person. `Guid?` permits no value. A missing
person and a person with an empty GUID are different states; do not replace
nulls with made-up IDs to silence the compiler.

In [CaptureContracts](../../src/Pulse.Api/Contracts/CaptureContracts.cs),
`CaptureRequest.Event` is `string?` because an HTTP caller can omit it. The
endpoint validates before producing `IncomingEvent`. The `!` operator only
silences a compiler warning; it does not check anything at runtime.

`CaptureResponse` is a record: a concise data carrier with value equality.
`Project` is a mutable entity tracked for database persistence. Use a record
to describe a response; understand identity and mutation before treating a
database entity like an immutable value.

**Try:** inspect what happens when `event` is missing, empty, and whitespace.
Write your expected status for each before running `CaptureEndpointsTests`.

## Collections and LINQ

`Where` describes filtering; `Select` describes the output shape;
`OrderBy` describes order; `Take` limits a sequence. On an EF query these
can become SQL. After `ToListAsync`, later LINQ operates on in-memory data.

Read `GetMetricsAsync` in
[IngestionOperationsService](../../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs).
Find the project filter, aggregation, and point where the database is read.
Explain why pulling all queued payload strings into a list just to count
them would do extra work.

**Try:** write a pure C# example containing three event timestamps. Filter a
range and sort them. Then find a database query doing the same kind of work.
Do not assume every C# operation can be translated by EF.

## Async and cancellation

`Task<T>` represents a result that may not yet exist. `await` lets a method
resume after it completes. It does not promise a new thread or make shared
mutable state safe. The scoped database context should not be used for
multiple concurrent queries via `Task.WhenAll`.

`CancellationToken ct` carries a request to stop. Trace it from an endpoint
to `ToListAsync(ct)`. In the worker, cancellation during shutdown is expected
control flow. Observe the catch filter that excludes cancellation from
ordinary failure handling.

**Try:** explain the difference between a malformed payload, a temporary
storage failure, and a cancellation request. Which should consume retries?

## Dependencies and lifetimes

[Program.cs](../../src/Pulse.Api/Program.cs) wires objects together.
`AddScoped` provides a service within a scope. The worker creates a scope
because it has a longer lifetime than one request. The signal is a singleton
so producers and the consumer share a bell.

Read the worker and draw which objects are created once versus for each
batch. Microsoft documents the same scope pattern for
[background services](https://learn.microsoft.com/en-us/dotnet/core/extensions/scoped-service).

**Done when:** you can explain `?`, `!`, `record`, `await`, `ct`, and a scoped
dependency using a specific example here. Your first refactoring exercise:
rename a confusing local variable, run the affected tests, and verify that
your diff changes no behavior.
