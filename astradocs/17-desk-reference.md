# Pulse desk reference

Keep this open while navigating. Follow a link when the short explanation
is not enough.

## The system in one line

`Product -> capture -> durable queue -> worker -> stored events -> queries`

The processing path can also produce dead letters.

## Five anchors

| Word | Meaning |
| --- | --- |
| Project | Groups analytics data and access |
| User | Operates the management API |
| Person | Represents tracked product activity |
| QueuedEvent | Pending work saved in the database |
| AnalyticsEvent | Successfully stored activity used by queries |

## Where to look

| Question | File or guide |
| --- | --- |
| What starts everything? | [Program.cs](../src/Pulse.Api/Program.cs) |
| What JSON does capture accept? | [CaptureContracts.cs](../src/Pulse.Api/Contracts/CaptureContracts.cs) |
| Where is queued work added? | [CaptureEndpoints.cs](../src/Pulse.Api/Endpoints/CaptureEndpoints.cs) |
| Who wakes and processes it? | [IngestionWorker.cs](../src/Pulse.Api/Ingestion/IngestionWorker.cs), [IngestionPipeline.cs](../src/Pulse.Infrastructure/Services/IngestionPipeline.cs) |
| Who stores events and resolves people? | [CaptureService.cs](../src/Pulse.Infrastructure/Services/CaptureService.cs), [IdentityService.cs](../src/Pulse.Infrastructure/Services/IdentityService.cs) |
| Who calculates answers? | [QueryService.cs](../src/Pulse.Infrastructure/Services/QueryService.cs) |
| Who checks project access? | [ProjectAccessService.cs](../src/Pulse.Api/Auth/ProjectAccessService.cs) |
| Where are runnable examples? | [Tests guide](11-tests-as-examples.md) |

## Say these carefully

- `202`: accepted queued work; check the later processing result.
- Empty queue: no pending rows; there could still be failed work.
- `distinct_id`: incoming label mapped to a Person within a project.
- Transaction: related database operations commit or roll back together.
- `await`: continue after the asynchronous operation completes.
- `ct`: cancellation token passed to work that supports cancellation.
- `??`: use the right value only when the left value is null.
- `!` after an expression: suppress a nullable warning; it does not validate data.

## My first debugging questions

Which project? Which credentials? Which event and UTC range? Was it queued?
Was it processed or dead-lettered? Which exact assertion or operation failed?

## Small commands from the repository root

```powershell
git status --short
dotnet test --filter "FullyQualifiedName~TimeBucketTests"
dotnet run --project src/Pulse.Api
```

Start the API before running `./scripts/learning-demo.ps1` in another
terminal. See [first-session setup](../docs/learning/01-first-session.md)
for PATH and PowerShell instructions.

Need another explanation? [Return to the AstraDocs index](README.md).
