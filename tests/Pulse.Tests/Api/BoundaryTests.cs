using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Api.Endpoints;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

/// <summary>
/// Input boundaries and recovery paths: the batch-size limit on both sides,
/// property payloads that are not objects, cursor stability while data is
/// being written, empty exports, and rate-limit recovery.
/// </summary>
public class BoundaryTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public BoundaryTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Batch-size limit ------------------------------------------------------

    [Fact]
    public async Task Capture_BatchOfExactly1000_IsAccepted()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        var batch = Enumerable.Range(0, CaptureEndpoints.MaxBatchSize)
            .Select(i => (object)new { @event = "bulk", distinct_id = $"bulk-{i % 25}" })
            .ToArray();

        var response = await _client.PostAsJsonAsync(
            "/capture", new { api_key = apiKey, batch });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaptureResponse>();
        Assert.Equal(CaptureEndpoints.MaxBatchSize, body!.Queued);

        await TestIngestion.WaitForDrainAsync(_client, TimeSpan.FromSeconds(60));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(CaptureEndpoints.MaxBatchSize, db.Events.Count(e => e.ProjectId == projectId));
    }

    [Fact]
    public async Task Capture_BatchOf1001_IsRejectedWhole()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        var batch = Enumerable.Range(0, CaptureEndpoints.MaxBatchSize + 1)
            .Select(i => (object)new { @event = "bulk", distinct_id = $"over-{i}" })
            .ToArray();

        var response = await _client.PostAsJsonAsync(
            "/capture", new { api_key = apiKey, batch });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("maximum", await response.Content.ReadAsStringAsync());

        // Nothing from the oversized batch was queued.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, db.QueuedEvents.Count(q => q.ProjectId == projectId));
        Assert.Equal(0, db.Events.Count(e => e.ProjectId == projectId));
    }

    // --- Property payload shapes ---------------------------------------------------

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public async Task Capture_NonObjectProperties_AreCoercedToEmpty(string propertiesJson)
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        using var properties = JsonDocument.Parse(propertiesJson);
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            @event = "odd-props",
            distinct_id = "odd-user",
            properties = properties.RootElement,
        });

        // Non-object properties are dropped, not fatal: the event survives.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await TestIngestion.WaitForDrainAsync(_client);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var stored = db.Events.Single(e => e.ProjectId == projectId && e.Name == "odd-props");
        Assert.Equal("{}", stored.PropertiesJson);
    }

    // --- Cursor pagination under writes ------------------------------------------------

    [Fact]
    public async Task ExportCursor_NeitherSkipsNorDuplicates_WhenRowsArriveMidScan()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("page", "u1", "2026-03-01T10:00:00Z"),
            Ev("page", "u2", "2026-03-01T11:00:00Z"),
            Ev("page", "u3", "2026-03-01T13:00:00Z"));

        var page1 = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=2");
        var cursor = page1.GetProperty("nextCursor").GetString()!;

        // A new event lands between the cursor position and the remaining
        // rows while the client is mid-export.
        await CaptureBatchAsync(apiKey, Ev("page", "u4", "2026-03-01T12:00:00Z"));

        var page2 = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=10&cursor={Uri.EscapeDataString(cursor)}");

        var ids1 = page1.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        var ids2 = page2.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

        // Pages never overlap, and every pre-existing row appears exactly once.
        Assert.Empty(ids1.Intersect(ids2));
        Assert.Equal(2, ids1.Count);
        Assert.Equal(2, ids2.Count); // u4 (12:00) and u3 (13:00)
        Assert.Equal(
            ["u4", "u3"],
            page2.GetProperty("events").EnumerateArray()
                .Select(e => e.GetProperty("distinctId").GetString()));
    }

    [Fact]
    public async Task ExportCursor_IsStableAcrossIdenticalRequests()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("page", "u1", "2026-03-01T10:00:00Z"),
            Ev("page", "u2", "2026-03-01T11:00:00Z"));

        var first = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=1");
        var second = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?limit=1");

        Assert.Equal(
            first.GetProperty("nextCursor").GetString(),
            second.GetProperty("nextCursor").GetString());
        Assert.Equal(
            first.GetProperty("events")[0].GetProperty("id").GetGuid(),
            second.GetProperty("events")[0].GetProperty("id").GetGuid());
    }

    // --- Empty exports ---------------------------------------------------------------

    [Fact]
    public async Task ExportEvents_EmptyProject_ReturnsEmptyJsonPage()
    {
        var (projectId, _) = await CreateProjectAsync();

        var page = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events");

        Assert.Empty(page.GetProperty("events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ExportEvents_EmptyProject_CsvIsHeaderOnly()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/export/events?format=csv");
        response.EnsureSuccessStatusCode();

        var csv = await response.Content.ReadAsStringAsync();
        Assert.Equal("id,timestamp,event,distinct_id,person_id,properties", csv.TrimEnd('\n'));
    }

    [Fact]
    public async Task ExportEvents_FilterMatchingNothing_ReturnsEmptyPage()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("page", "u1", "2026-03-01T10:00:00Z"));

        var filters = """[{"property":"url","operator":"equals","value":"/nowhere"}]""";
        var page = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/export/events?filters={Uri.EscapeDataString(filters)}");

        Assert.Empty(page.GetProperty("events").EnumerateArray());
    }

    // --- Rate limiting --------------------------------------------------------------

    [Fact]
    public async Task Capture_RateLimit_RecoversAfterTheWindowResets()
    {
        // Tiny dedicated limiter: 2 requests per 1-second window.
        using var limitedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Capture:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Capture:WindowSeconds", "1");
        });
        using var client = limitedFactory.CreateClient();

        var payload = new
        {
            api_key = "pk_live_ffffffffffffffffffffffffffffffff",
            @event = "x",
            distinct_id = "u1",
        };

        // Exhaust the window (401: unknown key, but the request was admitted)...
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/capture", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/capture", payload)).StatusCode);

        // ...the third is throttled...
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/capture", payload)).StatusCode);

        // ...and once the window rolls over, capture works again.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/capture", payload)).StatusCode);
    }

    // --- Helpers ---------------------------------------------------------------

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Boundary {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }

    private async Task CaptureBatchAsync(string apiKey, params object[] events)
    {
        var response = await _client.PostAsJsonAsync(
            "/capture", new { api_key = apiKey, batch = events });
        response.EnsureSuccessStatusCode();
        await TestIngestion.WaitForDrainAsync(_client);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
