using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class FilterBreakdownTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public FilterBreakdownTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    // --- Trend filters ------------------------------------------------------

    [Fact]
    public async Task Trend_EventPropertyEqualsFilter_CountsOnlyMatches()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/pricing" }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { url = "/docs" }),
            Ev("pageview", "u3", "2026-03-02T12:00:00Z", new { url = "/pricing" }));

        var filters = """[{"property":"url","operator":"equals","value":"/pricing"}]""";
        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            $"&filters={Uri.EscapeDataString(filters)}");

        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(2, bucket.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Trend_ContainsAndIsSetFilters_Work()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/docs/api" }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { url = "/blog" }),
            Ev("pageview", "u3", "2026-03-02T12:00:00Z", new { referrer = "google" }));

        var contains = """[{"property":"url","operator":"contains","value":"DOCS"}]""";
        var trend = await GetAsync<JsonElement>(TrendUrl(projectId, contains));
        Assert.Equal(1, TotalCount(trend));

        var isNotSet = """[{"property":"url","operator":"is_not_set"}]""";
        trend = await GetAsync<JsonElement>(TrendUrl(projectId, isNotSet));
        Assert.Equal(1, TotalCount(trend));
    }

    [Fact]
    public async Task Trend_PersonPropertyFilter_FollowsThePerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // u-pro sets plan=pro on signup, then views a page.
            Ev("signup", "u-pro", "2026-03-01T10:00:00Z",
                new Dictionary<string, object> { ["$set"] = new { plan = "pro" } }),
            Ev("pageview", "u-pro", "2026-03-02T10:00:00Z", new { url = "/a" }),
            // u-free is on the free plan.
            Ev("signup", "u-free", "2026-03-01T11:00:00Z",
                new Dictionary<string, object> { ["$set"] = new { plan = "free" } }),
            Ev("pageview", "u-free", "2026-03-02T11:00:00Z", new { url = "/a" }));

        var filters = """[{"property":"plan","operator":"equals","value":"pro","type":"person"}]""";
        var trend = await GetAsync<JsonElement>(TrendUrl(projectId, filters));

        Assert.Equal(1, TotalCount(trend));
    }

    [Fact]
    public async Task Trend_InvalidFilterOperator_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var filters = """[{"property":"url","operator":"regex","value":"x"}]""";
        var response = await _client.GetAsync(TrendUrl(projectId, filters));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Trend_MalformedFiltersJson_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(TrendUrl(projectId, "{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Breakdowns ---------------------------------------------------------

    [Fact]
    public async Task Trend_Breakdown_ReturnsTopNAndAggregatesOther()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/a" }),
            Ev("pageview", "u2", "2026-03-02T10:05:00Z", new { url = "/a" }),
            Ev("pageview", "u3", "2026-03-02T10:10:00Z", new { url = "/a" }),
            Ev("pageview", "u1", "2026-03-02T11:00:00Z", new { url = "/b" }),
            Ev("pageview", "u2", "2026-03-02T11:05:00Z", new { url = "/b" }),
            Ev("pageview", "u1", "2026-03-02T12:00:00Z", new { url = "/c" }),
            Ev("pageview", "u2", "2026-03-02T13:00:00Z", new { url = "/d" }));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            "&breakdown=url&breakdownLimit=2");

        var series = trend.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(3, series.Count);
        Assert.Equal("/a", series[0].GetProperty("value").GetString());
        Assert.Equal(3, series[0].GetProperty("total").GetInt32());
        Assert.Equal("/b", series[1].GetProperty("value").GetString());
        Assert.Equal(2, series[1].GetProperty("total").GetInt32());
        Assert.Equal("(other)", series[2].GetProperty("value").GetString());
        Assert.Equal(2, series[2].GetProperty("total").GetInt32());
        Assert.Equal("url", trend.GetProperty("breakdown").GetString());
    }

    [Fact]
    public async Task Trend_Breakdown_MissingPropertyGroupsUnderNone()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/a" }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { }));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day&breakdown=url");

        var values = trend.GetProperty("series").EnumerateArray()
            .Select(s => s.GetProperty("value").GetString()).ToList();
        Assert.Contains("/a", values);
        Assert.Contains("(none)", values);
    }

    [Fact]
    public async Task Trend_Breakdown_CombinesWithFilters()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/a", device = "mobile" }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { url = "/b", device = "desktop" }));

        var filters = """[{"property":"device","operator":"equals","value":"mobile"}]""";
        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            $"&breakdown=url&filters={Uri.EscapeDataString(filters)}");

        var series = trend.GetProperty("series").EnumerateArray().ToList();
        Assert.Single(series);
        Assert.Equal("/a", series[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Trend_InvalidBreakdownLimit_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/insights/trend?event=x&breakdown=url&breakdownLimit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Funnel & retention filters ------------------------------------------

    [Fact]
    public async Task Funnel_WithEventFilter_OnlyCountsMatchingEvents()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // u1's whole journey is on mobile.
            Ev("signup", "u1", "2026-03-01T10:00:00Z", new { device = "mobile" }),
            Ev("activate", "u1", "2026-03-02T10:00:00Z", new { device = "mobile" }),
            // u2 signs up on desktop, activates on mobile: desktop-filtered funnel
            // should see the signup only.
            Ev("signup", "u2", "2026-03-01T11:00:00Z", new { device = "desktop" }),
            Ev("activate", "u2", "2026-03-02T11:00:00Z", new { device = "mobile" }));

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-10T00:00:00Z",
                filters = new[] { new { property = "device", @operator = "equals", value = "mobile" } },
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 1], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task Funnel_WithPersonFilter_RestrictsToMatchingPersons()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("signup", "pro-user", "2026-03-01T10:00:00Z",
                new Dictionary<string, object> { ["$set"] = new { plan = "pro" } }),
            Ev("activate", "pro-user", "2026-03-02T10:00:00Z", new { }),
            Ev("signup", "free-user", "2026-03-01T11:00:00Z",
                new Dictionary<string, object> { ["$set"] = new { plan = "free" } }),
            Ev("activate", "free-user", "2026-03-02T11:00:00Z", new { }));

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-10T00:00:00Z",
                filters = new[]
                {
                    new { property = "plan", @operator = "equals", value = "pro", type = "person" },
                },
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 1], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task Retention_WithFilter_OnlyCountsMatchingActivity()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // u1 is active in the app both days.
            Ev("visit", "u1", "2026-03-01T10:00:00Z", new { source = "app" }),
            Ev("visit", "u1", "2026-03-02T10:00:00Z", new { source = "app" }),
            // u2's day-1 return came from an email link, filtered out.
            Ev("visit", "u2", "2026-03-01T11:00:00Z", new { source = "app" }),
            Ev("visit", "u2", "2026-03-02T11:00:00Z", new { source = "email" }));

        var filters = """[{"property":"source","operator":"equals","value":"app"}]""";
        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-01&days=2" +
            $"&filters={Uri.EscapeDataString(filters)}");

        var day0 = retention.GetProperty("cohorts").EnumerateArray().First();
        Assert.Equal(2, day0.GetProperty("size").GetInt32());
        Assert.Equal(
            [2, 1],
            day0.GetProperty("returnedByDay").EnumerateArray().Select(v => v.GetInt32()));
    }

    // --- Helpers ---------------------------------------------------------------

    private string TrendUrl(Guid projectId, string filters) =>
        $"/api/projects/{projectId}/insights/trend?event=pageview" +
        "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
        $"&filters={Uri.EscapeDataString(filters)}";

    private static int TotalCount(JsonElement trend) =>
        trend.GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("count").GetInt32());

    private static object Ev(string name, string distinctId, string timestamp, object properties) =>
        new { @event = name, distinct_id = distinctId, timestamp, properties };

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Filters {Guid.NewGuid():N}" });
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
}
