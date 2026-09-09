# Where things live

**Keep one sentence:** Api handles HTTP, Infrastructure handles storage and
service work, Domain holds entities and pure rules, and Tests check behavior.

```text
pulse-net/
  Pulse.slnx                 solution: groups the C# projects
  src/
    Pulse.Api/               HTTP app; starts in Program.cs
      Contracts/             request and response shapes
      Endpoints/             routes and their handlers
      Auth/                  tokens and project-access checks
      Ingestion/, Export/    background worker loops
    Pulse.Infrastructure/    database context and services
      Services/
    Pulse.Domain/            entities, rules, calculations
      Entities/
  tests/Pulse.Tests/         runnable behavior examples
  docs/                     reference docs and engineering curriculum
  astradocs/                this gradual onboarding companion
  scripts/                  local learning demo
```

A `.csproj` file describes a C# project and its references. A `.cs` file
contains C# code. `bin` and `obj` directories, when present, contain generated
build output; start reading source outside those directories.

## An address book for your first task

| You are investigating... | Open first | Follow next |
| --- | --- | --- |
| App startup | [Program](../src/Pulse.Api/Program.cs) | [startup guide](07-startup-and-wiring.md) |
| Event submission | [CaptureEndpoints](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs) | [CaptureContracts](../src/Pulse.Api/Contracts/CaptureContracts.cs) |
| Queued processing | [IngestionWorker](../src/Pulse.Api/Ingestion/IngestionWorker.cs) | [IngestionPipeline](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs) |
| Person identity | [IdentityService](../src/Pulse.Infrastructure/Services/IdentityService.cs) | [PersonDistinctId](../src/Pulse.Domain/Entities/PersonDistinctId.cs) |
| Counts and charts | [InsightEndpoints](../src/Pulse.Api/Endpoints/InsightEndpoints.cs) | [QueryService](../src/Pulse.Infrastructure/Services/QueryService.cs) |
| Access denied | [ProjectAccessService](../src/Pulse.Api/Auth/ProjectAccessService.cs) | [AuthzMatrixTests](../tests/Pulse.Tests/Api/AuthzMatrixTests.cs) |
| Table mapping/indexes | [PulseDbContext](../src/Pulse.Infrastructure/PulseDbContext.cs) | [Domain entities](../src/Pulse.Domain/Entities) |
| Replay | [IngestionEndpoints](../src/Pulse.Api/Endpoints/IngestionEndpoints.cs) | [IngestionOperationsService](../src/Pulse.Infrastructure/Services/IngestionOperationsService.cs) |

## Find it without knowing its filename

Use your editor's repository search, or run these from the root:

```powershell
rg -n 'MapPost\("/capture"' src
rg -n 'IngestQueuedAsync' src tests
rg -n 'RequireMemberAsync' src/Pulse.Api
```

`rg` searches text. `-n` includes line numbers. A match tells you where to
look, not whether that line causes a bug. If `rg` is unavailable, your
editor's Find in Files works too.

**Try:** find both the caller and implementation of `IngestQueuedAsync`.
Explain the difference: one asks for work; the other implements it.

Next: [follow one event through those files](04-one-event-story.md).
