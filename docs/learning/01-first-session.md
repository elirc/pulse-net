# First session: make the system observable

For the ongoing implementation, use [bootcamp session 1](../../astradocs/bootcamp/01-practice-tools.md).
The demo now accepts `-EventName signup -EventCount 5`; the default remains two
`learning_pageview` events. It prints project/ingestion/dead-letter URLs after
verification. These require a separately supplied member token; the generated
practice token is not exported.

**Prerequisite:** a terminal, an editor, and the .NET 10 SDK.
**Outcome:** explain why a successful capture request does not yet mean an
event is queryable. Stop after any complete predict-run-explain loop and return
when you are ready for the next one.

## 1. Establish your starting point

From the repository root:

```powershell
git status --short
dotnet --version
dotnet test --filter "FullyQualifiedName~TimeBucketTests"
```

If Windows says `dotnet` is unknown, check your local installation:

```powershell
Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe"
# If True, this changes PATH only for the current terminal:
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet --version
```

If it is absent, install the .NET 10 SDK before continuing. A runtime alone
cannot build this repository. Read the first failing error before changing code.

Open [TimeBucket.cs](../../src/Pulse.Domain/TimeBucket.cs) and
[TimeBucketTests.cs](../../tests/Pulse.Tests/Domain/TimeBucketTests.cs).
Pick one test: predict its result, run it, and identify the assertion. A
test is a small executable claim, not simply code that runs without throwing.

## 2. Make one request go through the whole backend

Use a database created only for this exercise. In terminal A, from the repository
root, choose a fresh temporary path, point the API at it, build, and explicitly
apply the schema before startup:

```powershell
$practiceDb = Join-Path $env:TEMP ("pulse-learning-" + [guid]::NewGuid().ToString("N") + ".db")
$env:ConnectionStrings__Pulse = "Data Source=$practiceDb"
dotnet build
dotnet src/Pulse.Api/bin/Debug/net10.0/Pulse.Api.dll db upgrade --database $practiceDb
dotnet run --project src/Pulse.Api
```

Ordinary startup verifies the schema and refuses a missing, legacy, or pending
database; it does not apply upgrades for you. Keep terminal A open so the
connection-string setting applies to the API process. For valuable or existing
data, stop and follow the [database upgrade runbook](../runbooks/database-upgrades.md)
instead of using this practice sequence.

In terminal B:

```powershell
./scripts/learning-demo.ps1
```

If Windows reports that script execution is disabled, run the reviewed local
script with a policy override for that process only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/learning-demo.ps1
```

This does not change the machine or user execution-policy setting.

The demo uses localhost by default, creates a fresh practice account and
project, queues two events, waits for that project's queue to drain, and
checks a trend. It prints observations without printing credentials.
Those practice records remain in your local database. A different local
port can be passed as `-BaseUrl http://localhost:YOUR_PORT`.

Before running it, predict: why is `202` different from `200`? Could a queue
reach zero even if the event never became queryable?

## 3. Follow the code in this order

1. [CaptureRequest](../../src/Pulse.Api/Contracts/CaptureContracts.cs): JSON names become C# properties.
2. [CaptureEndpoints](../../src/Pulse.Api/Endpoints/CaptureEndpoints.cs): key check, validation, queue insert, response.
3. [IngestionWorker](../../src/Pulse.Api/Ingestion/IngestionWorker.cs): creates a scope and asks the processor for work.
4. [IngestionProcessor](../../src/Pulse.Infrastructure/Services/IngestionProcessor.cs): reads queue rows and classifies outcomes.
5. [CaptureService](../../src/Pulse.Infrastructure/Services/CaptureService.cs): resolves identity, writes event and definitions, acknowledges the queue row.
6. [QueryService](../../src/Pulse.Infrastructure/Services/QueryService.cs): reads stored events to answer a trend.

Place a breakpoint at the queue's `SaveChangesAsync` and another at
`IngestQueuedAsync`. Step over the request's save. Inspect the difference
between `QueuedEvents` and `Events`; they represent different states.

## Exercise

Change the demo's event name and its matching trend query. Predict the count
if you change only one of them. Run the demo and explain what you see.
Then send an event with a blank `distinct_id` using the getting-started
examples. Identify where the request is rejected.

**Done when:** you can draw `HTTP -> queue -> worker -> event -> query`,
explain the write key versus the management token, and distinguish "queued",
"processed", and "dead-lettered" without looking at these notes.

**Check your reasoning:** queue depth zero means no pending rows; failures
also leave the queue. You must check the event or its query result as well.

When finished, stop the API before deleting the disposable database. The value
of `$practiceDb` exists only in terminal A; inspect it before removal so you do
not confuse it with another database. The demo intentionally leaves its practice
records available until you remove that file.
