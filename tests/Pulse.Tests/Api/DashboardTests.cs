using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class DashboardTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public DashboardTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Dashboard_CrudAndTiles_RoundTrip()
    {
        var (projectId, _) = await CreateProjectAsync();
        var insight = await SaveInsightAsync(projectId, "Signups", "trend", new { @event = "signup" });

        // Create + rename.
        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards",
            new { name = "KPIs", description = "Company KPIs" });
        Assert.Equal("KPIs", dashboard.Name);
        Assert.Empty(dashboard.Tiles);

        var renamed = await PutAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}",
            new { name = "Company KPIs" });
        Assert.Equal("Company KPIs", renamed.Name);
        Assert.Equal("Company KPIs", renamed.Description);

        // Add a tile with layout metadata.
        var tile = await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles",
            new { insightId = insight, layout = new { x = 0, y = 0, w = 6, h = 4 } });
        Assert.Equal("Signups", tile.InsightName);
        Assert.Equal(6, tile.Layout.GetProperty("w").GetInt32());

        // Move the tile.
        var moved = await PutAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles/{tile.Id}",
            new { layout = new { x = 6, y = 0, w = 6, h = 4 } });
        Assert.Equal(6, moved.Layout.GetProperty("x").GetInt32());

        // Detail view includes the tile; list view counts it.
        var fetched = await GetAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}");
        Assert.Single(fetched.Tiles);

        var list = await GetAsync<List<DashboardSummaryResponse>>(
            $"/api/projects/{projectId}/dashboards");
        Assert.Single(list);
        Assert.Equal(1, list[0].TileCount);

        // Remove tile, delete dashboard.
        var deleteTile = await _client.DeleteAsync(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles/{tile.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteTile.StatusCode);

        var deleteDashboard = await _client.DeleteAsync(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteDashboard.StatusCode);
        Assert.Empty(await GetAsync<List<DashboardSummaryResponse>>(
            $"/api/projects/{projectId}/dashboards"));
    }

    [Fact]
    public async Task AddTile_WithForeignInsight_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();
        var (otherProjectId, _) = await CreateProjectAsync();
        var foreignInsight = await SaveInsightAsync(otherProjectId, "Foreign", "trend", new { @event = "x" });

        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards", new { name = "Mine" });

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles",
            new { insightId = foreignInsight });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ExecutesEveryTileQuery_InOneResponse()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("signup", "u2", "2026-03-02T10:00:00Z"),
            Ev("activate", "u1", "2026-03-02T11:00:00Z"));

        var trendInsight = await SaveInsightAsync(projectId, "Signups", "trend", new
        {
            @event = "signup",
            from = "2026-03-01T00:00:00Z",
            to = "2026-03-03T00:00:00Z",
            interval = "day",
        });
        var funnelInsight = await SaveInsightAsync(projectId, "Activation", "funnel", new
        {
            steps = new[] { "signup", "activate" },
            from = "2026-03-01T00:00:00Z",
            to = "2026-03-10T00:00:00Z",
        });

        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards", new { name = "Growth" });
        await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles",
            new { insightId = trendInsight, layout = new { x = 0 } });
        await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles",
            new { insightId = funnelInsight, layout = new { x = 6 } });

        var refresh = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/refresh", new { });

        var tiles = refresh.GetProperty("tiles").EnumerateArray().ToList();
        Assert.Equal(2, tiles.Count);

        // Trend tile: 2 signups across the buckets, no error.
        var trendTile = tiles[0];
        Assert.True(trendTile.GetProperty("error").ValueKind == JsonValueKind.Null);
        var counts = trendTile.GetProperty("result").GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("count").GetInt32());
        Assert.Equal(2, counts);

        // Funnel tile: 2 signed up, 1 activated.
        var funnelTile = tiles[1];
        var steps = funnelTile.GetProperty("result").GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([2, 1], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task Refresh_RespectsInsightFilters()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvProps("pageview", "u1", "2026-03-01T10:00:00Z", new { url = "/pricing" }),
            EvProps("pageview", "u2", "2026-03-01T11:00:00Z", new { url = "/docs" }));

        var insight = await SaveInsightAsync(projectId, "Pricing views", "trend", new
        {
            @event = "pageview",
            from = "2026-03-01T00:00:00Z",
            to = "2026-03-02T00:00:00Z",
            filters = new[] { new { property = "url", @operator = "equals", value = "/pricing" } },
        });

        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards", new { name = "Filtered" });
        await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles",
            new { insightId = insight });

        var refresh = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/refresh", new { });

        var tile = refresh.GetProperty("tiles").EnumerateArray().Single();
        var counts = tile.GetProperty("result").GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("count").GetInt32());
        Assert.Equal(1, counts);
    }

    [Fact]
    public async Task Refresh_BrokenTileReportsError_OthersStillSucceed()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("signup", "u1", "2026-03-01T10:00:00Z"));

        var broken = await SaveInsightAsync(projectId, "Broken", "trend", new { });  // no event
        var healthy = await SaveInsightAsync(projectId, "Healthy", "trend", new
        {
            @event = "signup",
            from = "2026-03-01T00:00:00Z",
            to = "2026-03-02T00:00:00Z",
        });

        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards", new { name = "Mixed" });
        await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles", new { insightId = broken });
        await PostAsync<DashboardTileResponse>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/tiles", new { insightId = healthy });

        var refresh = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/dashboards/{dashboard.Id}/refresh", new { });

        var tiles = refresh.GetProperty("tiles").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, tiles[0].GetProperty("result").ValueKind);
        Assert.Contains("event", tiles[0].GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, tiles[1].GetProperty("error").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, tiles[1].GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Dashboards_AreInvisibleToNonMembers()
    {
        var (projectId, _) = await CreateProjectAsync();
        var dashboard = await PostAsync<DashboardResponse>(
            $"/api/projects/{projectId}/dashboards", new { name = "Private" });

        var intruder = await _factoryClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{projectId}/dashboards")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.PostAsJsonAsync(
                $"/api/projects/{projectId}/dashboards/{dashboard.Id}/refresh", new { })).StatusCode);
    }

    // --- Helpers ---------------------------------------------------------------

    private async Task<HttpClient> _factoryClientAsync()
    {
        var client = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        return client;
    }

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private static object EvProps(string name, string distinctId, string timestamp, object properties) =>
        new { @event = name, distinct_id = distinctId, timestamp, properties };

    private async Task<Guid> SaveInsightAsync(Guid projectId, string name, string type, object config)
    {
        var insight = await PostAsync<InsightResponse>(
            $"/api/projects/{projectId}/insights",
            new { name, type, config });
        return insight.Id;
    }

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Dashboards {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }

    private async Task CaptureBatchAsync(string apiKey, params object[] events)
    {
        var response = await _client.PostAsJsonAsync(
            "/capture", new { api_key = apiKey, batch = events });
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PutAsync<T>(string url, object payload)
    {
        var response = await _client.PutAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
