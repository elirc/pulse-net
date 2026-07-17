using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

/// <summary>
/// Failure-path behavior of the ingestion pipeline: permanent vs transient
/// poison classification, retry accounting, metrics accuracy, same-distinct-id
/// ordering, and change-tracker isolation between rows.
/// </summary>
public class IngestionResilienceTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public IngestionResilienceTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Poison classification ----------------------------------------------------

    [Fact]
    public async Task PermanentPoison_DeadLettersImmediately_WithoutRetries()
    {
        var (projectId, _) = await CreateProjectAsync();

        // Unparseable payload: permanent, no retry budget consumed.
        EnqueueRaw(projectId, "not even json");
        RingAndWait();
        await TestIngestion.WaitForDrainAsync(_client);

        var letters = await GetAsync<List<DeadLetterResponse>>(
            $"/api/projects/{projectId}/ingestion/dead-letters");
        var letter = Assert.Single(letters);
        Assert.Equal(0, letter.Attempts);
        Assert.Contains("validation", letter.Error);
    }

    [Fact]
    public async Task VanishedProject_IsPermanent_AndDeadLetters()
    {
        // A queued row whose project no longer exists cannot ever succeed.
        var ghostProjectId = Guid.NewGuid();
        EnqueueRaw(ghostProjectId, """{"Name":"e","DistinctId":"u1","Timestamp":null,"PropertiesJson":"{}"}""");
        RingAndWait();
        await TestIngestion.WaitForDrainAsync(_client);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var letter = db.DeadLetterEvents.Single(d => d.ProjectId == ghostProjectId);
        Assert.Contains("no longer exists", letter.Error);
        Assert.False(db.QueuedEvents.Any(q => q.ProjectId == ghostProjectId));
    }

    [Fact]
    public async Task TransientPoison_RetriesUpToMaxAttempts_ThenDeadLetters()
    {
        var (projectId, _) = await CreateProjectAsync();

        // The envelope deserializes and validates, but the properties JSON
        // blows up inside the capture pipeline — a transient-classified
        // failure that burns all MaxAttempts before dead-lettering.
        EnqueueRaw(projectId, """{"Name":"boom","DistinctId":"u1","Timestamp":null,"PropertiesJson":"{corrupt"}""");
        RingAndWait();
        await TestIngestion.WaitForDrainAsync(_client, TimeSpan.FromSeconds(30));

        var letters = await GetAsync<List<DeadLetterResponse>>(
            $"/api/projects/{projectId}/ingestion/dead-letters");
        var letter = Assert.Single(letters);
        Assert.Contains($"Failed after {IngestionProcessor.MaxAttempts} attempts", letter.Error);
        Assert.Equal(IngestionProcessor.MaxAttempts - 1, letter.Attempts);
    }

    // --- Change-tracker isolation ---------------------------------------------------

    [Fact]
    public async Task RowsAfterAFailedRow_ProcessNormally_InTheSameBatch()
    {
        var (projectId, _) = await CreateProjectAsync();

        // valid → transient poison → valid, all in one queue batch. The
        // failed row's half-tracked entities must not leak into its
        // neighbors' SaveChanges.
        EnqueueRaw(projectId, """{"Name":"before","DistinctId":"iso-user","Timestamp":"2026-03-01T10:00:00+00:00","PropertiesJson":"{}"}""");
        EnqueueRaw(projectId, """{"Name":"poison","DistinctId":"iso-user","Timestamp":null,"PropertiesJson":"{corrupt"}""");
        EnqueueRaw(projectId, """{"Name":"after","DistinctId":"iso-user","Timestamp":"2026-03-01T11:00:00+00:00","PropertiesJson":"{}"}""");
        RingAndWait();
        await TestIngestion.WaitForDrainAsync(_client, TimeSpan.FromSeconds(30));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var events = db.Events.Where(e => e.ProjectId == projectId).ToList();

        // Exactly the two valid events, each persisted exactly once.
        Assert.Equal(2, events.Count);
        Assert.Single(events, e => e.Name == "before");
        Assert.Single(events, e => e.Name == "after");
        Assert.Single(db.DeadLetterEvents.Where(d => d.ProjectId == projectId).ToList());

        // Both valid events resolved to the same person, exactly one person.
        Assert.Equal(1, db.Persons.Count(p => p.ProjectId == projectId));
    }

    // --- Ordering -------------------------------------------------------------------

    [Fact]
    public async Task SameDistinctId_SetProperties_ApplyInEnqueueOrder()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Three $set writes to the same key, one batch: last write must win.
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                EvSet("step", "order-user", new { plan = "first" }),
                EvSet("step", "order-user", new { plan = "second" }),
                EvSet("step", "order-user", new { plan = "third" }),
            },
        });
        response.EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(_client);

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/order-user");
        Assert.Equal("third", person.Properties.GetProperty("plan").GetString());
    }

    [Fact]
    public async Task SameDistinctId_AcrossSeparateCaptureCalls_KeepsEnqueueOrder()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Two capture calls back-to-back with no drain in between; the queue's
        // Seq order is the arrival order, so the later value wins.
        var first = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[] { EvSet("a", "seq-user", new { stage = "early" }) },
        });
        first.EnsureSuccessStatusCode();
        var second = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[] { EvSet("b", "seq-user", new { stage = "late" }) },
        });
        second.EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(_client);

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/seq-user");
        Assert.Equal("late", person.Properties.GetProperty("stage").GetString());
    }

    // --- Metrics accuracy -------------------------------------------------------------

    [Fact]
    public async Task Metrics_CountProcessedAndDeadLettered_Exactly()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        var before = await GetAsync<IngestionMetricsResponse>("/api/ingestion/metrics");

        // Two good events over HTTP plus one permanent poison row.
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "m1", distinct_id = "mu1" },
                new { @event = "m2", distinct_id = "mu2" },
            },
        });
        response.EnsureSuccessStatusCode();
        EnqueueRaw(projectId, "{permanent poison");
        RingAndWait();
        await TestIngestion.WaitForDrainAsync(_client);

        var after = await GetAsync<IngestionMetricsResponse>("/api/ingestion/metrics");
        Assert.Equal(0, after.Pending);
        Assert.Equal(before.ProcessedTotal + 2, after.ProcessedTotal);
        Assert.Equal(before.DeadLetteredTotal + 1, after.DeadLetteredTotal);
    }

    [Fact]
    public async Task PeriodicSweep_DrainsRows_EvenWithoutASignal()
    {
        var (projectId, _) = await CreateProjectAsync();

        var before = await GetAsync<IngestionMetricsResponse>("/api/ingestion/metrics");

        // Insert directly and never ring the bell: only the worker's periodic
        // sweep can pick these up.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            for (var i = 0; i < 5; i++)
            {
                db.QueuedEvents.Add(new QueuedEvent
                {
                    ProjectId = projectId,
                    PayloadJson = $$"""{"Name":"depth-{{i}}","DistinctId":"du","Timestamp":null,"PropertiesJson":"{}"}""",
                });
            }

            db.SaveChanges();
        }

        await TestIngestion.WaitForDrainAsync(_client);

        var after = await GetAsync<IngestionMetricsResponse>("/api/ingestion/metrics");
        Assert.Equal(0, after.Pending);
        Assert.Equal(before.ProcessedTotal + 5, after.ProcessedTotal);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(5, checkDb.Events.Count(e => e.ProjectId == projectId));
    }

    // --- Helpers ---------------------------------------------------------------

    private static object EvSet(string name, string distinctId, object set) =>
        new
        {
            @event = name,
            distinct_id = distinctId,
            properties = new Dictionary<string, object> { ["$set"] = set },
        };

    private void EnqueueRaw(Guid projectId, string payloadJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.QueuedEvents.Add(new QueuedEvent
        {
            ProjectId = projectId,
            PayloadJson = payloadJson,
        });
        db.SaveChanges();
    }

    private void RingAndWait() =>
        _factory.Services.GetRequiredService<IngestionSignal>().Ring();

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Resilience {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
