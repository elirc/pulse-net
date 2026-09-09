[CmdletBinding()]
param(
    [uri]$BaseUrl = 'http://localhost:5141',
    [string]$EventName = 'learning_pageview',
    [ValidateRange(1, 10)]
    [int]$EventCount = 2,
    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$EventName = $EventName.Trim()
if ([string]::IsNullOrWhiteSpace($EventName) -or $EventName.Length -gt 200) {
    throw 'EventName must contain 1 to 200 characters after trimming.'
}
if (-not $BaseUrl.IsLoopback -or $BaseUrl.Scheme -notin @('http', 'https')) {
    throw 'This learning demo creates practice data. Use a local HTTP(S) API URL.'
}
$base = $BaseUrl.AbsoluteUri.TrimEnd('/')

function Invoke-Pulse {
    param([string]$Method, [string]$Path, [hashtable]$Headers = @{}, [object]$Body)
    $request = @{
        Method = $Method
        Uri = "$base$Path"
        Headers = $Headers
        TimeoutSec = $TimeoutSeconds
    }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 10 -Compress
    }
    Invoke-RestMethod @request
}

Write-Host '1. Checking the local API. Start it with: dotnet run --project src/Pulse.Api'
$null = Invoke-Pulse -Method GET -Path '/health'

Write-Host '2. Registering a practice account. Its token grants management access.'
$suffix = [guid]::NewGuid().ToString('N')
$auth = Invoke-Pulse -Method POST -Path '/api/auth/register' -Body @{
    email = "learner-$suffix@example.test"
    password = "practice-$suffix"
    name = 'Pulse learner'
}
$management = @{ Authorization = "Bearer $($auth.token)" }
$project = Invoke-Pulse -Method POST -Path '/api/projects' -Headers $management -Body @{
    name = "Learning session $suffix"
}
$projectPath = "/api/projects/$($project.id)"

Write-Host "3. Capturing $EventCount '$EventName' events with the write key. Accepted work is queued work."
$timestamp = [DateTimeOffset]::UtcNow
$events = @(
    for ($index = 0; $index -lt $EventCount; $index++) {
        $page = if ($index -eq 0) { '/intro' } elseif ($index -eq 1) { '/transactions' } else { "/practice/$($index + 1)" }
        @{ event = $EventName; distinct_id = 'learner-1'; timestamp = $timestamp.ToString('o'); properties = @{ page = $page } }
    }
)
$capture = Invoke-Pulse -Method POST -Path '/capture' -Headers @{ 'X-Api-Key' = $project.apiKey } -Body @{
    batch = $events
}
if ($capture.status -ne 'queued' -or $capture.queued -ne $EventCount) {
    throw "Expected $EventCount queued events."
}

Write-Host '4. Waiting for this project only. Zero pending does not rule out dead letters.'
$waitTimer = [System.Diagnostics.Stopwatch]::StartNew()
do {
    $metrics = Invoke-Pulse -Method GET -Path "$projectPath/ingestion/metrics" -Headers $management
    if ($metrics.pending -eq 0) { break }
    if ($waitTimer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        throw "Queue did not drain within $TimeoutSeconds seconds; inspect ingestion metrics and logs."
    }
    Start-Sleep -Milliseconds 100
} while ($true)
if ($metrics.deadLetters -ne 0) { throw 'Processing produced dead letters. Inspect the project ingestion endpoint.' }

Write-Host "5. Querying with the read key. Expected count $EventCount, uniquePersons 1."
$from = [uri]::EscapeDataString($timestamp.AddMinutes(-1).ToString('o'))
$to = [uri]::EscapeDataString($timestamp.AddMinutes(1).ToString('o'))
$encodedEvent = [uri]::EscapeDataString($EventName)
$trend = Invoke-Pulse -Method GET -Path "$projectPath/insights/trend?event=$encodedEvent&from=$from&to=$to&interval=day" -Headers @{ 'X-Api-Key' = $project.readKey }
$count = ($trend.buckets | Measure-Object -Property count -Sum).Sum
$people = ($trend.buckets | Measure-Object -Property uniquePersons -Sum).Sum
if ($count -ne $EventCount -or $people -ne 1) {
    throw "Unexpected trend: count=$count, uniquePersons=$people. Inspect the query range and ingestion state."
}

Write-Host "Verified: $count events, $people person. Practice project: $($project.id)"
Write-Host 'Follow-up routes (requires a member Bearer token; the generated session token is not exported):'
Write-Host "  Project:      $base$projectPath"
Write-Host "  Ingestion:    $base$projectPath/ingestion/metrics"
Write-Host "  Dead letters: $base$projectPath/ingestion/dead-letters?limit=20"
Write-Host 'Teach back: where was durability established, and why did we check both dead letters and the trend?'
Write-Host 'Next: docs/learning/04-data-and-transactions.md. This practice account and project remain in your local database.'
