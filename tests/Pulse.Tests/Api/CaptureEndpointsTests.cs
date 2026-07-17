using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class CaptureEndpointsTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public CaptureEndpointsTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Capture_SingleEvent_PersistsWithProperties()
    {
        var apiKey = await CreateProjectAsync("Single Capture");

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            @event = "pageview",
            distinct_id = "anon-123",
            timestamp = "2026-02-01T10:30:00Z",
            properties = new { url = "/pricing", plan = "free" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaptureResponse>();
        Assert.Equal(1, body!.Ingested);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var stored = db.Events.Single(e => e.DistinctId == "anon-123");
        Assert.Equal("pageview", stored.Name);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 10, 30, 0, TimeSpan.Zero), stored.Timestamp);
        Assert.Contains("\"url\"", stored.PropertiesJson);
        Assert.Contains("/pricing", stored.PropertiesJson);
    }

    [Fact]
    public async Task Capture_Batch_PersistsAllEvents()
    {
        var apiKey = await CreateProjectAsync("Batch Capture");

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "signup", distinct_id = "u-1" },
                new { @event = "signup", distinct_id = "u-2" },
                new { @event = "purchase", distinct_id = "u-1", properties = new { amount = 42 } },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CaptureResponse>();
        Assert.Equal(3, body!.Ingested);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(2, db.Events.Count(e => e.Name == "signup"));
        Assert.Equal(1, db.Events.Count(e => e.Name == "purchase"));
    }

    [Fact]
    public async Task Capture_ApiKeyViaHeader_IsAccepted()
    {
        var apiKey = await CreateProjectAsync("Header Auth");

        var request = new HttpRequestMessage(HttpMethod.Post, "/capture")
        {
            Content = JsonContent.Create(new { @event = "login", distinct_id = "u-9" }),
        };
        request.Headers.Add("X-Api-Key", apiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Capture_UnknownApiKey_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = "pk_live_00000000000000000000000000000000",
            @event = "pageview",
            distinct_id = "anon-1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Capture_MissingApiKey_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            @event = "pageview",
            distinct_id = "anon-1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Capture_MissingEventName_Returns400()
    {
        var apiKey = await CreateProjectAsync("Bad Event");

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            distinct_id = "anon-1",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Capture_BatchWithInvalidItem_RejectsWholeBatch()
    {
        var apiKey = await CreateProjectAsync("Bad Batch");

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "ok-event", distinct_id = "u-1" },
                new { @event = "", distinct_id = "u-2" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, db.Events.Count(e => e.Name == "ok-event"));
    }

    [Fact]
    public async Task Capture_EmptyBatch_Returns400()
    {
        var apiKey = await CreateProjectAsync("Empty Batch");

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Capture_WithoutTimestamp_DefaultsToServerTime()
    {
        var apiKey = await CreateProjectAsync("Default Timestamp");
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            @event = "no-ts",
            distinct_id = "u-ts",
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var stored = db.Events.Single(e => e.Name == "no-ts");
        Assert.InRange(stored.Timestamp, before, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        return project!.ApiKey;
    }
}
