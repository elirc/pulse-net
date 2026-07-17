using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

/// <summary>
/// Correctness edge cases for the query engine: funnel ordering and window
/// boundaries, retention day boundaries and UTC bucketing, trend zero-fill
/// across month boundaries, and breakdown tie/none semantics.
/// </summary>
public class QueryEdgeCaseTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public QueryEdgeCaseTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    // --- Funnel ordering ---------------------------------------------------------

    [Fact]
    public async Task Funnel_SameTimestampSteps_CountAsOrderedProgression()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Both steps land on the exact same instant (batched SDK events often
        // share a timestamp): ties resolve by step order, so the funnel
        // converts deterministically.
        await CaptureBatchAsync(apiKey,
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("activate", "u1", "2026-03-01T10:00:00Z"));

        var steps = await FunnelStepsAsync(projectId, ["signup", "activate"]);

        Assert.Equal([1, 1], steps);
    }

    [Fact]
    public async Task Funnel_RepeatedStepEvents_DoNotAdvanceTheFunnel()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Signing up three times is still only step 1.
        await CaptureBatchAsync(apiKey,
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("signup", "u1", "2026-03-01T11:00:00Z"),
            Ev("signup", "u1", "2026-03-01T12:00:00Z"));

        var steps = await FunnelStepsAsync(projectId, ["signup", "activate"]);

        Assert.Equal([1, 0], steps);
    }

    [Fact]
    public async Task Funnel_RepeatedStepName_RequiresTheEventTwice()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            // u-twice views twice and converts a [view, view] funnel.
            Ev("view", "u-twice", "2026-03-01T10:00:00Z"),
            Ev("view", "u-twice", "2026-03-01T11:00:00Z"),
            // u-once only views once.
            Ev("view", "u-once", "2026-03-01T10:00:00Z"));

        var steps = await FunnelStepsAsync(projectId, ["view", "view"]);

        Assert.Equal([2, 1], steps);
    }

    [Fact]
    public async Task Funnel_StepsOutOfOrder_OnlyCountFromTheFirstStep()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Activation before signup does not count as a conversion.
        await CaptureBatchAsync(apiKey,
            Ev("activate", "u1", "2026-03-01T10:00:00Z"),
            Ev("signup", "u1", "2026-03-01T11:00:00Z"));

        var steps = await FunnelStepsAsync(projectId, ["signup", "activate"]);

        Assert.Equal([1, 0], steps);
    }

    [Fact]
    public async Task Funnel_ConversionExactlyAtWindowBoundary_StillCounts()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            // u-exact converts exactly 24h after step 1 (windowDays = 1).
            Ev("signup", "u-exact", "2026-03-01T10:00:00Z"),
            Ev("activate", "u-exact", "2026-03-02T10:00:00Z"),
            // u-late converts one second past the window.
            Ev("signup", "u-late", "2026-03-01T10:00:00Z"),
            Ev("activate", "u-late", "2026-03-02T10:00:01Z"));

        var steps = await FunnelStepsAsync(projectId, ["signup", "activate"], windowDays: 1);

        Assert.Equal([2, 1], steps);
    }

    [Fact]
    public async Task Funnel_WindowStartsAtTheEarliestFirstStep()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // The clock starts at the first signup; the second signup does not
        // restart the window, so the late activation is out.
        await CaptureBatchAsync(apiKey,
            Ev("signup", "u1", "2026-03-01T10:00:00Z"),
            Ev("signup", "u1", "2026-03-02T09:00:00Z"),
            Ev("activate", "u1", "2026-03-02T11:00:00Z"));

        var steps = await FunnelStepsAsync(projectId, ["signup", "activate"], windowDays: 1);

        Assert.Equal([1, 0], steps);
    }

    // --- Retention boundaries ------------------------------------------------------

    [Fact]
    public async Task Retention_DayZeroAndFinalDay_UseInclusiveStartExclusiveEnd()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            // Exactly at the range start: day-0 member.
            Ev("visit", "u-start", "2026-03-01T00:00:00Z"),
            // Last second of the final observed day: still inside.
            Ev("visit", "u-end", "2026-03-03T23:59:59Z"),
            // Exactly at the exclusive range end: outside.
            Ev("visit", "u-past", "2026-03-04T00:00:00Z"));

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-01&days=3");

        var cohorts = retention.GetProperty("cohorts").EnumerateArray().ToList();
        Assert.Equal(3, cohorts.Count);

        Assert.Equal(1, cohorts[0].GetProperty("size").GetInt32()); // u-start on 03-01
        Assert.Equal(0, cohorts[1].GetProperty("size").GetInt32());
        Assert.Equal(1, cohorts[2].GetProperty("size").GetInt32()); // u-end on 03-03
    }

    [Fact]
    public async Task Retention_ReturnDays_AreDayIndexedFromTheCohortDate()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            Ev("visit", "u1", "2026-03-01T09:00:00Z"),
            // Skips day 1, returns on day 2.
            Ev("visit", "u1", "2026-03-03T22:00:00Z"));

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-01&days=3");

        var day0 = retention.GetProperty("cohorts").EnumerateArray().First();
        Assert.Equal(
            [1, 0, 1],
            day0.GetProperty("returnedByDay").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public async Task Retention_LaterCohorts_HaveTriangularHorizons()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            Ev("visit", "u-late", "2026-03-03T12:00:00Z"));

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-01&days=3");

        var cohorts = retention.GetProperty("cohorts").EnumerateArray().ToList();
        Assert.Equal(3, cohorts[0].GetProperty("returnedByDay").GetArrayLength());
        Assert.Equal(2, cohorts[1].GetProperty("returnedByDay").GetArrayLength());
        Assert.Equal(1, cohorts[2].GetProperty("returnedByDay").GetArrayLength());
    }

    [Fact]
    public async Task Retention_BucketsByUtcDay_RegardlessOfClientOffset()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // 2026-03-08 is the US DST switch; events carry the local offsets a
        // client on Eastern time would send. Bucketing must follow UTC only:
        // 21:00 EST on 03-07 is already 02:00Z on 03-08.
        await CaptureBatchAsync(apiKey,
            Ev("visit", "u-est", "2026-03-07T21:00:00-05:00"),  // 03-08 02:00Z
            Ev("visit", "u-edt", "2026-03-08T03:30:00-04:00"),  // 03-08 07:30Z
            Ev("visit", "u-utc", "2026-03-08T23:00:00Z"));      // 03-08 23:00Z

        var retention = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/retention?from=2026-03-07&days=2");

        var cohorts = retention.GetProperty("cohorts").EnumerateArray().ToList();
        Assert.Equal(0, cohorts[0].GetProperty("size").GetInt32()); // nothing on 03-07 UTC
        Assert.Equal(3, cohorts[1].GetProperty("size").GetInt32()); // all three on 03-08 UTC
    }

    // --- Trend bucketing ---------------------------------------------------------

    [Fact]
    public async Task Trend_ZeroFillsEveryDay_AcrossAMonthBoundary()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // 2026 is not a leap year: Feb 28 is followed by Mar 1.
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-02-27T10:00:00Z"),
            Ev("pageview", "u2", "2026-03-01T10:00:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-02-26T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day");

        var buckets = trend.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(
            ["2026-02-26", "2026-02-27", "2026-02-28", "2026-03-01", "2026-03-02"],
            buckets.Select(b => b.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-dd")));
        Assert.Equal(
            [0, 1, 0, 1, 0],
            buckets.Select(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Trend_WeekBuckets_TruncateToMondayAcrossTheMonthBoundary()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // 2026-03-01 is a Sunday; its week starts Monday 2026-02-23.
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-01T10:00:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-02-23T00:00:00Z&to=2026-03-01T23:59:59Z&interval=week");

        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal("2026-02-23", bucket.GetProperty("start").GetDateTimeOffset().ToString("yyyy-MM-dd"));
        Assert.Equal(1, bucket.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Trend_HourBuckets_SplitEventsAcrossTheDayBoundary()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-01T23:59:59Z"),
            Ev("pageview", "u2", "2026-03-02T00:00:00Z"));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-01T23:00:00Z&to=2026-03-02T00:59:59Z&interval=hour");

        var buckets = trend.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(2, buckets.Count);
        Assert.Equal(1, buckets[0].GetProperty("count").GetInt32());
        Assert.Equal(1, buckets[1].GetProperty("count").GetInt32());
    }

    // --- Breakdown ties and (none) -----------------------------------------------

    [Fact]
    public async Task Breakdown_TopNTies_BreakDeterministicallyByOrdinalValue()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // /a and /b tie on two events each; limit 1 must keep /a (ordinal
        // tie-break) and push /b into (other) — stable across runs.
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = "/b" }),
            Ev("pageview", "u2", "2026-03-02T10:05:00Z", new { url = "/b" }),
            Ev("pageview", "u3", "2026-03-02T10:10:00Z", new { url = "/a" }),
            Ev("pageview", "u4", "2026-03-02T10:15:00Z", new { url = "/a" }));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            "&breakdown=url&breakdownLimit=1");

        var series = trend.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(2, series.Count);
        Assert.Equal("/a", series[0].GetProperty("value").GetString());
        Assert.Equal(2, series[0].GetProperty("total").GetInt32());
        Assert.Equal("(other)", series[1].GetProperty("value").GetString());
        Assert.Equal(2, series[1].GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Breakdown_JsonNullAndMissingProperty_BothGroupUnderNone()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { url = (string?)null }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { referrer = "google" }),
            Ev("pageview", "u3", "2026-03-02T12:00:00Z", new { url = "/a" }));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day&breakdown=url");

        var series = trend.GetProperty("series").EnumerateArray().ToList();
        var none = series.Single(s => s.GetProperty("value").GetString() == "(none)");
        Assert.Equal(2, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Breakdown_NoneCompetesForTopN_LikeAnyOtherValue()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // (none) has the most events, so at limit 1 it IS the top series.
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z", new { }),
            Ev("pageview", "u2", "2026-03-02T11:00:00Z", new { }),
            Ev("pageview", "u3", "2026-03-02T12:00:00Z", new { url = "/a" }));

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            "&breakdown=url&breakdownLimit=1");

        var series = trend.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal("(none)", series[0].GetProperty("value").GetString());
        Assert.Equal(2, series[0].GetProperty("total").GetInt32());
        Assert.Equal("(other)", series[1].GetProperty("value").GetString());
        Assert.Equal(1, series[1].GetProperty("total").GetInt32());
    }

    // --- Helpers ---------------------------------------------------------------

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private static object Ev(string name, string distinctId, string timestamp, object properties) =>
        new { @event = name, distinct_id = distinctId, timestamp, properties };

    private async Task<int[]> FunnelStepsAsync(
        Guid projectId, string[] steps, int windowDays = 14)
    {
        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps,
                windowDays,
                from = "2026-02-01T00:00:00Z",
                to = "2026-04-01T00:00:00Z",
            });

        return funnel.GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("persons").GetInt32())
            .ToArray();
    }

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"QueryEdge {Guid.NewGuid():N}" });
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
