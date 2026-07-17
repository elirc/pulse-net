using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class FeatureFlagTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public FeatureFlagTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    // --- CRUD ----------------------------------------------------------------

    [Fact]
    public async Task Flag_CrudRoundTrips()
    {
        var (projectId, _, _) = await CreateProjectAsync();

        var created = await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "new-onboarding", name = "New onboarding", type = "boolean", rolloutPercentage = 25 });
        Assert.Equal("boolean", created.Type);
        Assert.Equal(25, created.RolloutPercentage);
        Assert.True(created.Active);

        var fetched = await GetAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags/new-onboarding");
        Assert.Equal(created.Id, fetched.Id);

        var updated = await PutAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags/new-onboarding",
            new { rolloutPercentage = 75, active = false });
        Assert.Equal(75, updated.RolloutPercentage);
        Assert.False(updated.Active);

        var list = await GetAsync<List<FeatureFlagResponse>>(
            $"/api/projects/{projectId}/feature-flags");
        Assert.Single(list);

        var delete = await _client.DeleteAsync(
            $"/api/projects/{projectId}/feature-flags/new-onboarding");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Flag_DuplicateKey_Returns409()
    {
        var (projectId, _, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "dupe", type = "boolean" });

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "dupe", type = "boolean" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("has spaces", "boolean")]
    [InlineData("ok-key", "pie-chart")]
    public async Task Flag_InvalidKeyOrType_Returns400(string key, string type)
    {
        var (projectId, _, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/feature-flags", new { key, type });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MultivariateFlag_VariantsMustSumTo100()
    {
        var (projectId, _, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "bad-split",
                type = "multivariate",
                variants = new[]
                {
                    new { key = "a", rolloutPercentage = 60 },
                    new { key = "b", rolloutPercentage = 60 },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- /decide ---------------------------------------------------------------

    [Fact]
    public async Task Decide_FullRollout_ReturnsTrue_InactiveReturnsFalse()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "on-for-all", type = "boolean" });
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "switched-off", type = "boolean", active = false });

        var decide = await DecideAsync(apiKey, "any-user");

        Assert.True(decide.GetProperty("featureFlags").GetProperty("on-for-all").GetBoolean());
        Assert.False(decide.GetProperty("featureFlags").GetProperty("switched-off").GetBoolean());
    }

    [Fact]
    public async Task Decide_PartialRollout_IsDeterministicPerUser()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "half", type = "boolean", rolloutPercentage = 50 });

        var results = new Dictionary<string, bool>();
        for (var i = 0; i < 20; i++)
        {
            var decide = await DecideAsync(apiKey, $"user-{i}");
            results[$"user-{i}"] = decide.GetProperty("featureFlags").GetProperty("half").GetBoolean();
        }

        // Some users are in, some out...
        Assert.Contains(results.Values, v => v);
        Assert.Contains(results.Values, v => !v);

        // ...and every user gets the same answer on a second ask.
        foreach (var (user, expected) in results)
        {
            var again = await DecideAsync(apiKey, user);
            Assert.Equal(expected, again.GetProperty("featureFlags").GetProperty("half").GetBoolean());
        }
    }

    [Fact]
    public async Task Decide_MultivariateFlag_AssignsAStableVariant()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "ab-test",
                type = "multivariate",
                variants = new[]
                {
                    new { key = "control", rolloutPercentage = 50 },
                    new { key = "test", rolloutPercentage = 50 },
                },
            });

        var first = (await DecideAsync(apiKey, "user-1"))
            .GetProperty("featureFlags").GetProperty("ab-test").GetString();
        var second = (await DecideAsync(apiKey, "user-1"))
            .GetProperty("featureFlags").GetProperty("ab-test").GetString();

        Assert.Contains(first, new[] { "control", "test" });
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Decide_PersonPropertyTargeting_GatesTheFlag()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvSet("signup", "pro-user", new { plan = "pro" }),
            EvSet("signup", "free-user", new { plan = "free" }));

        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "pro-only",
                type = "boolean",
                filters = new[] { new { property = "plan", @operator = "equals", value = "pro", type = "person" } },
            });

        var pro = await DecideAsync(apiKey, "pro-user");
        var free = await DecideAsync(apiKey, "free-user");
        var stranger = await DecideAsync(apiKey, "never-seen");

        Assert.True(pro.GetProperty("featureFlags").GetProperty("pro-only").GetBoolean());
        Assert.False(free.GetProperty("featureFlags").GetProperty("pro-only").GetBoolean());
        Assert.False(stranger.GetProperty("featureFlags").GetProperty("pro-only").GetBoolean());
    }

    [Fact]
    public async Task Decide_CohortTargeting_GatesTheFlag()
    {
        var (projectId, apiKey, _) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("visit", "member"),
            Ev("visit", "outsider"));

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/member");
        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new { name = "Beta", type = "static", personIds = new[] { person.Id } });

        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "beta-only",
                type = "boolean",
                filters = new[] { new { type = "cohort", value = cohort.Id.ToString() } },
            });

        var member = await DecideAsync(apiKey, "member");
        var outsider = await DecideAsync(apiKey, "outsider");

        Assert.True(member.GetProperty("featureFlags").GetProperty("beta-only").GetBoolean());
        Assert.False(outsider.GetProperty("featureFlags").GetProperty("beta-only").GetBoolean());
    }

    [Fact]
    public async Task Decide_WithInvalidKey_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/decide", new
        {
            api_key = "pk_live_ffffffffffffffffffffffffffffffff",
            distinct_id = "u1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Local evaluation ---------------------------------------------------------

    [Fact]
    public async Task LocalEvaluation_ReturnsFullDefinitions_WithReadKey()
    {
        var (projectId, _, readKey) = await CreateProjectAsync();
        await PostAsync<FeatureFlagResponse>(
            $"/api/projects/{projectId}/feature-flags",
            new
            {
                key = "local",
                type = "multivariate",
                rolloutPercentage = 40,
                variants = new[]
                {
                    new { key = "a", rolloutPercentage = 30 },
                    new { key = "b", rolloutPercentage = 70 },
                },
            });

        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/projects/{projectId}/feature-flags/local-evaluation");
        request.Headers.Add("X-Api-Key", readKey);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var flag = payload.GetProperty("flags").EnumerateArray().Single();
        Assert.Equal("local", flag.GetProperty("key").GetString());
        Assert.Equal(40, flag.GetProperty("rolloutPercentage").GetDouble());
        Assert.Equal(2, flag.GetProperty("variants").EnumerateArray().Count());
    }

    // --- Helpers --------------------------------------------------------------------

    private static object Ev(string name, string distinctId) =>
        new { @event = name, distinct_id = distinctId };

    private static object EvSet(string name, string distinctId, object set) =>
        new
        {
            @event = name,
            distinct_id = distinctId,
            properties = new Dictionary<string, object> { ["$set"] = set },
        };

    private async Task<JsonElement> DecideAsync(string apiKey, string distinctId)
    {
        var response = await _client.PostAsJsonAsync(
            "/decide", new { api_key = apiKey, distinct_id = distinctId });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(Guid ProjectId, string ApiKey, string ReadKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Flags {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey, project.ReadKey);
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
