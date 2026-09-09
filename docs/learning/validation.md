# Validation record for the learning and ingestion changes

Environment: Windows, local .NET SDK 10.0.400. This records observations from
the implementation session; it is not a claim that the final suite is green.

## Established results

- An earlier revision's 14 new ingestion operations and transaction tests
  passed, including concurrent replay, project isolation, invalid payloads,
  and database rollback after injected acknowledgement/enqueue failures.
- The PowerShell demo ran against an isolated local SQLite database and
  verified two captured events, one person, zero pending rows, and no dead
  letters. It used a process-scoped script execution-policy override.
- Documentation relative file links and `git diff --check` passed.
- The final `dotnet build --no-restore --nologo` succeeded with zero errors,
  including the new replay validation and additional tests. Two pre-existing
  CS8714 warnings remain in QueryService's retention
  dictionaries, whose keys are nullable GUIDs.

## Unresolved runtime validation

The first full suite run passed 304 tests and failed
`BoundaryTests.Capture_BatchOfExactly1000_IsAccepted` at its 60-second queue
drain deadline. The same test also timed out in isolation. Concurrency alone
therefore does not explain the failure. No baseline comparison established
whether this is an existing environment-sensitive failure or a regression.

A later targeted run could not load the rebuilt `Pulse.Infrastructure.dll`:
Windows reported `An Application Control policy has blocked this file`
(`0x800711C7`). Its seven selected tests failed during initialization, before
their behavior assertions. No application-control setting was changed.

A provisional test-parallelism cap and a tracker-cleanup performance
experiment were removed. The final code retains the transaction fix and
recovery features; it does not claim to resolve the large-batch timeout.
The final small replay-validation additions and reset-budget test have
compiled but do not yet have a passing runtime result. The repository's two
passing full-suite runs remain required.

## Next verification session

Use an environment whose application-control policy permits the built
project assemblies. Do not disable machine protections as part of a test.

```powershell
dotnet test --filter "FullyQualifiedName~IngestionTransactionTests|FullyQualifiedName~IngestionOperationsTests"
dotnet test --filter "FullyQualifiedName~Capture_BatchOfExactly1000_IsAccepted"
dotnet test
dotnet test
```

The drain helper now includes the last observed metrics in timeout errors.
Use those metrics and worker diagnostics to distinguish slow progress,
stalled storage, and repeated processing failures. Compare the same batch
test against the original revision under equivalent conditions before
assigning a cause. Preserve the timeout and event-count assertions while
investigating; a longer timeout alone would not establish correctness.
