using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

/// <summary>
/// Flag evaluation against imperfect targeting data: deleted and stale
/// cohorts, dynamic cohorts whose membership moves under the flag, and
/// rollout stability when the percentage changes.
/// </summary>
public class FlagTargetingEdgeTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public FlagTargetingEdgeTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Decide_FlagTargetingADeletedCohort_FailsClosed()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("visit", "member"));

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/member");
        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new { name = "Doomed", type = "static", personIds = new[] { person.Id } });

        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "doomed-cohort",
                type = "boolean",
                filters = new[] { new { type = "cohort", value = cohort.Id.ToString() } },
            });

        // While the cohort exists the member is in.
        Assert.True((await DecideFlagAsync(apiKey, "member", "doomed-cohort")).GetBoolean());

        // Delete the cohort out from under the flag: it must fail closed for
        // everyone, not throw or fail open.
        var delete = await _client.DeleteAsync($"/api/projects/{projectId}/cohorts/{cohort.Id}");
        delete.EnsureSuccessStatusCode();

        var after = await DecideFlagAsync(apiKey, "member", "doomed-cohort");
        Assert.False(after.GetBoolean());
    }

    [Fact]
    public async Task Decide_FlagTargetingANonexistentCohortId_FailsClosed()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("visit", "anyone"));

        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "ghost-cohort",
                type = "boolean",
                filters = new[] { new { type = "cohort", value = Guid.NewGuid().ToString() } },
            });

        var flag = await DecideFlagAsync(apiKey, "anyone", "ghost-cohort");
        Assert.False(flag.GetBoolean());
    }

    [Fact]
    public async Task Decide_DynamicCohortFlag_EvaluatesLiveMembership()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();

        // u-recent was active today; u-stale's only activity is 30 days old.
        var now = DateTimeOffset.UtcNow;
        await CaptureBatchAsync(apiKey,
            EvAt("visit", "u-recent", now.AddHours(-1)),
            EvAt("visit", "u-stale", now.AddDays(-30)));

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Active this week",
                type = "dynamic",
                rules = new[] { new { kind = "performed_event", @event = "visit", days = 7 } },
            });

        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "active-only",
                type = "boolean",
                filters = new[] { new { type = "cohort", value = cohort.Id.ToString() } },
            });

        Assert.True((await DecideFlagAsync(apiKey, "u-recent", "active-only")).GetBoolean());
        Assert.False((await DecideFlagAsync(apiKey, "u-stale", "active-only")).GetBoolean());

        // The stale user becomes active again: the very next decide sees the
        // membership change without any cohort refresh step.
        await CaptureBatchAsync(apiKey, EvAt("visit", "u-stale", now));
        Assert.True((await DecideFlagAsync(apiKey, "u-stale", "active-only")).GetBoolean());
    }

    [Fact]
    public async Task Decide_RolloutIncrease_KeepsExistingUsersOn()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "ramp", type = "boolean", rolloutPercentage = 30 });

        var onAt30 = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            if ((await DecideFlagAsync(apiKey, $"user-{i}", "ramp")).GetBoolean())
            {
                onAt30.Add($"user-{i}");
            }
        }

        await PutAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags/ramp",
            new { rolloutPercentage = 60 });

        // Nobody who had the flag loses it when the rollout widens.
        foreach (var user in onAt30)
        {
            Assert.True((await DecideFlagAsync(apiKey, user, "ramp")).GetBoolean());
        }
    }

    [Fact]
    public async Task Decide_UnknownDistinctId_StillGetsRolloutFlags()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "everyone", type = "boolean" });

        // No person exists for this id; untargeted flags evaluate anyway.
        var flag = await DecideFlagAsync(apiKey, "never-captured", "everyone");
        Assert.True(flag.GetBoolean());
    }

    // --- Helpers ---------------------------------------------------------------

    private static object Ev(string name, string distinctId) =>
        new { @event = name, distinct_id = distinctId };

    private static object EvAt(string name, string distinctId, DateTimeOffset timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp = timestamp.ToString("O") };

    private async Task<JsonElement> DecideFlagAsync(string apiKey, string distinctId, string flagKey)
    {
        var response = await _client.PostAsJsonAsync(
            "/decide", new { api_key = apiKey, distinct_id = distinctId });
        response.EnsureSuccessStatusCode();
        var decide = await response.Content.ReadFromJsonAsync<JsonElement>();
        return decide.GetProperty("featureFlags").GetProperty(flagKey);
    }

    private async Task<(Guid ProjectId, string ApiKey, string ReadKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"FlagEdge {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey, project.ReadKey);
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

    private async Task<T> PutAsync<T>(string url, object payload)
    {
        var response = await _client.PutAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
