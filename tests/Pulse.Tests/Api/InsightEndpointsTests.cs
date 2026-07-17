using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class InsightEndpointsTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public InsightEndpointsTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    // --- Trends -----------------------------------------------------------

    [Fact]
    public async Task Trend_CountsEventsPerDay_AndZeroFillsGaps()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z"),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z"),
            Ev("pageview", "u1", "2026-03-04T09:00:00Z"),
            Ev("other", "u1", "2026-03-03T09:00:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend" +
            "?event=pageview&from=2026-03-01T00:00:00Z&to=2026-03-04T23:59:59Z&interval=day");

        var buckets = trend.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(4, buckets.Count);
        Assert.Equal(new[] { 0, 2, 0, 1 }, buckets.Select(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Trend_CountsUniquePersons_NotJustEvents()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("login", "solo-user", "2026-03-02T10:00:00Z"),
            Ev("login", "solo-user", "2026-03-02T12:00:00Z"),
            Ev("login", "solo-user", "2026-03-02T14:00:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend" +
            "?event=login&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day");

        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(3, bucket.GetProperty("count").GetInt32());
        Assert.Equal(1, bucket.GetProperty("uniquePersons").GetInt32());
    }

    [Fact]
    public async Task Trend_HourInterval_BucketsWithinOneDay()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("tick", "u1", "2026-03-02T10:15:00Z"),
            Ev("tick", "u1", "2026-03-02T10:45:00Z"),
            Ev("tick", "u1", "2026-03-02T12:05:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend" +
            "?event=tick&from=2026-03-02T10:00:00Z&to=2026-03-02T12:59:00Z&interval=hour");

        var counts = trend.GetProperty("buckets").EnumerateArray()
            .Select(b => b.GetProperty("count").GetInt32()).ToList();
        Assert.Equal([2, 0, 1], counts);
    }

    [Fact]
    public async Task Trend_InvalidInterval_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/insights/trend?event=x&interval=fortnight");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Trend_MissingEvent_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{projectId}/insights/trend");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Trend_UnknownProject_Returns404()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.GetAsync(
            $"/api/projects/{Guid.NewGuid()}/insights/trend?event=x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Funnels ----------------------------------------------------------

    [Fact]
    public async Task Funnel_ComputesOrderedStepConversion()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // u1 completes all three steps in order.
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("activate", "u1", "2026-03-02T10:00:00Z"),
            Ev("purchase", "u1", "2026-03-03T10:00:00Z"),
            // u2 signs up and activates only.
            Ev("signup", "u2", "2026-03-01T11:00:00Z"),
            Ev("activate", "u2", "2026-03-01T12:00:00Z"),
            // u3 signs up only.
            Ev("signup", "u3", "2026-03-01T13:00:00Z"),
            // u4 purchases without signing up first: no funnel entry.
            Ev("purchase", "u4", "2026-03-01T14:00:00Z"));

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate", "purchase" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-31T00:00:00Z",
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], steps.Select(s => s.GetProperty("persons").GetInt32()));
        Assert.Equal(1.0, steps[0].GetProperty("conversionFromPrevious").GetDouble());
        Assert.Equal(0.6667, steps[1].GetProperty("conversionFromPrevious").GetDouble(), 4);
        Assert.Equal(0.5, steps[2].GetProperty("conversionFromPrevious").GetDouble());
        Assert.Equal(0.3333, steps[2].GetProperty("conversionFromFirst").GetDouble(), 4);
    }

    [Fact]
    public async Task Funnel_OutOfOrderSteps_DoNotCount()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // Activation happens BEFORE signup: step 2 must not count.
            Ev("activate", "backwards", "2026-03-01T09:00:00Z"),
            Ev("signup", "backwards", "2026-03-01T10:00:00Z"));

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-02T00:00:00Z",
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 0], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task Funnel_StepOutsideConversionWindow_DoesNotCount()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("signup", "slowpoke", "2026-03-01T10:00:00Z"),
            Ev("activate", "slowpoke", "2026-03-20T10:00:00Z")); // 19 days later

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-31T00:00:00Z",
                windowDays = 7,
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 0], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task Funnel_FewerThanTwoSteps_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/insights/funnel",
            new { steps = new[] { "only-one" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Retention --------------------------------------------------------

    [Fact]
    public async Task Retention_TracksDayNReturn()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            // Day-0 cohort (Mar 1): u1 and u2.
            Ev("visit", "u1", "2026-03-01T10:00:00Z"),
            Ev("visit", "u2", "2026-03-01T11:00:00Z"),
            // u1 returns on day 1 and day 2; u2 never returns.
            Ev("visit", "u1", "2026-03-02T10:00:00Z"),
            Ev("visit", "u1", "2026-03-03T10:00:00Z"),
            // Day-1 cohort (Mar 2): u3, returns day 1.
            Ev("visit", "u3", "2026-03-02T12:00:00Z"),
            Ev("visit", "u3", "2026-03-03T12:00:00Z"));

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-01&days=3");

        var cohorts = retention.GetProperty("cohorts").EnumerateArray().ToList();
        Assert.Equal(3, cohorts.Count);

        var day0 = cohorts[0];
        Assert.Equal(2, day0.GetProperty("size").GetInt32());
        Assert.Equal(
            [2, 1, 1],
            day0.GetProperty("returnedByDay").EnumerateArray().Select(v => v.GetInt32()));

        var day1 = cohorts[1];
        Assert.Equal(1, day1.GetProperty("size").GetInt32());
        Assert.Equal(
            [1, 1],
            day1.GetProperty("returnedByDay").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public async Task Retention_WithTargetEvent_LimitsCohortEntry()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("signup", "member", "2026-04-01T10:00:00Z"),
            Ev("visit", "lurker", "2026-04-01T11:00:00Z"),
            Ev("visit", "member", "2026-04-02T10:00:00Z"));

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-04-01&days=2&targetEvent=signup");

        var day0 = retention.GetProperty("cohorts").EnumerateArray().First();
        Assert.Equal(1, day0.GetProperty("size").GetInt32()); // Only 'member' signed up.
        Assert.Equal(
            [1, 1],
            day0.GetProperty("returnedByDay").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public async Task Retention_InvalidDays_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/insights/retention?days=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Saved insights ---------------------------------------------------

    [Fact]
    public async Task SaveAndListInsights_RoundTrips()
    {
        var (projectId, _) = await CreateProjectAsync();

        var created = await PostAsync<InsightResponse>(
            $"/api/projects/{projectId}/insights",
            new
            {
                name = "Weekly signups",
                type = "trend",
                config = new { @event = "signup", interval = "week" },
            });

        Assert.Equal("Weekly signups", created.Name);
        Assert.Equal("trend", created.Type);

        var list = await GetAsync<List<InsightResponse>>($"/api/projects/{projectId}/insights");
        Assert.Single(list);
        Assert.Equal(created.Id, list[0].Id);
        Assert.Equal("signup", list[0].Config.GetProperty("event").GetString());

        var fetched = await GetAsync<InsightResponse>(
            $"/api/projects/{projectId}/insights/{created.Id}");
        Assert.Equal(created.Id, fetched.Id);
    }

    [Fact]
    public async Task SaveInsight_InvalidType_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/insights",
            new { name = "Bad", type = "pie-chart" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Helpers ----------------------------------------------------------

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Insights {Guid.NewGuid():N}" });
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

    private async Task<T> PostAsync<T>(string url, object payload)
    {
        var response = await _client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
