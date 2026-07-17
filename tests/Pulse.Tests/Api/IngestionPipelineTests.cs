using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class IngestionPipelineTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public IngestionPipelineTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Capture_Returns202_AndTheWorkerPersistsAsynchronously()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "pageview", distinct_id = "async-1", timestamp = "2026-03-02T10:00:00Z" },
                new { @event = "pageview", distinct_id = "async-2", timestamp = "2026-03-02T11:00:00Z" },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaptureResponse>();
        Assert.Equal("queued", body!.Status);
        Assert.Equal(2, body.Queued);

        await TestIngestion.WaitForDrainAsync(_client);

        var trend = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day");
        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(2, bucket.GetProperty("count").GetInt32());
        Assert.Equal(2, bucket.GetProperty("uniquePersons").GetInt32());
    }

    [Fact]
    public async Task PoisonEvent_IsDeadLettered_NotRetriedForever()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Inject a poison row directly (the endpoint validates shape, so
        // garbage can only arrive via bugs or schema drift).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.QueuedEvents.Add(new QueuedEvent
            {
                ProjectId = projectId,
                PayloadJson = "{this is not json",
            });
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IngestionSignal>();
        }

        _factory.Services.GetRequiredService<IngestionSignal>().Ring();
        await TestIngestion.WaitForDrainAsync(_client);

        // A valid event through the same pipeline still works.
        var ok = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            @event = "healthy",
            distinct_id = "u1",
        });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        await TestIngestion.WaitForDrainAsync(_client);

        var letters = await _client.GetFromJsonAsync<List<DeadLetterResponse>>(
            $"/api/projects/{projectId}/ingestion/dead-letters");
        var letter = Assert.Single(letters!);
        Assert.Equal("{this is not json", letter.PayloadJson);
        Assert.Contains("validation", letter.Error);
    }

    [Fact]
    public async Task ValidationFailure_AtProcessing_IsDeadLettered()
    {
        var (projectId, _) = await CreateProjectAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.QueuedEvents.Add(new QueuedEvent
            {
                ProjectId = projectId,
                // Valid JSON, but blank event name fails processor validation.
                PayloadJson = """{"Name":"","DistinctId":"u1","Timestamp":null,"PropertiesJson":"{}"}""",
            });
            await db.SaveChangesAsync();
        }

        _factory.Services.GetRequiredService<IngestionSignal>().Ring();
        await TestIngestion.WaitForDrainAsync(_client);

        var letters = await _client.GetFromJsonAsync<List<DeadLetterResponse>>(
            $"/api/projects/{projectId}/ingestion/dead-letters");
        Assert.Contains(letters!, l => l.Error.Contains("distinct_id"));
    }

    [Fact]
    public async Task Metrics_ReportQueueDepthAndTotals()
    {
        var (_, apiKey) = await CreateProjectAsync();

        var before = await _client.GetFromJsonAsync<JsonElement>("/api/ingestion/metrics");
        var processedBefore = before.GetProperty("processedTotal").GetInt64();

        await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "m1", distinct_id = "u1" },
                new { @event = "m2", distinct_id = "u2" },
                new { @event = "m3", distinct_id = "u3" },
            },
        });

        await TestIngestion.WaitForDrainAsync(_client);

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/ingestion/metrics");
        Assert.Equal(0, after.GetProperty("pending").GetInt32());
        Assert.True(after.GetProperty("processedTotal").GetInt64() >= processedBefore + 3);
    }

    [Fact]
    public async Task DeadLetters_AreInvisibleToNonMembers()
    {
        var (projectId, _) = await CreateProjectAsync();

        var intruder = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(intruder);

        var response = await intruder.GetAsync(
            $"/api/projects/{projectId}/ingestion/dead-letters");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IdentifyOrdering_SurvivesTheQueue()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Anonymous browsing, then signup + identify — enqueued in order.
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "pageview", distinct_id = "device-1" },
                new
                {
                    @event = "signup",
                    distinct_id = "device-1",
                    properties = new Dictionary<string, object> { ["$set"] = new { email = "a@b.c" } },
                },
                new
                {
                    @event = "$identify",
                    distinct_id = "user-1",
                    properties = new Dictionary<string, object> { ["$anon_distinct_id"] = "device-1" },
                },
                new { @event = "purchase", distinct_id = "user-1" },
            },
        });
        response.EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(_client);

        var person = await _client.GetFromJsonAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-1");

        Assert.NotNull(person);
        Assert.Contains("device-1", person.DistinctIds);
        Assert.Contains("user-1", person.DistinctIds);
        Assert.Equal("a@b.c", person.Properties.GetProperty("email").GetString());
    }

    // --- Helpers ---------------------------------------------------------------

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Ingestion {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }
}
