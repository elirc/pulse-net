using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class CohortTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public CohortTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    // --- Static cohorts -----------------------------------------------------

    [Fact]
    public async Task StaticCohort_CrudAndMembership()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("visit", "u1", "2026-03-02T10:00:00Z"),
            Ev("visit", "u2", "2026-03-02T11:00:00Z"),
            Ev("visit", "u3", "2026-03-02T12:00:00Z"));

        var p1 = await PersonIdAsync(projectId, "u1");
        var p2 = await PersonIdAsync(projectId, "u2");
        var p3 = await PersonIdAsync(projectId, "u3");

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new { name = "Beta testers", type = "static", personIds = new[] { p1, p2 } });
        Assert.Equal("static", cohort.Type);

        var members = await GetAsync<CohortMembersResponse>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons");
        Assert.Equal(2, members.Count);
        Assert.Contains(p1, members.PersonIds);

        // Add u3, remove u1.
        await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons",
            new { personIds = new[] { p3 } });
        var remove = await _client.DeleteAsync(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons/{p1}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        members = await GetAsync<CohortMembersResponse>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons");
        Assert.Equal(2, members.Count);
        Assert.DoesNotContain(p1, members.PersonIds);
        Assert.Contains(p3, members.PersonIds);

        // List + delete.
        var list = await GetAsync<List<CohortResponse>>($"/api/projects/{projectId}/cohorts");
        Assert.Single(list);

        var delete = await _client.DeleteAsync($"/api/projects/{projectId}/cohorts/{cohort.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await GetAsync<List<CohortResponse>>($"/api/projects/{projectId}/cohorts"));
    }

    // --- Dynamic cohorts ------------------------------------------------------

    [Fact]
    public async Task DynamicCohort_PropertyRule_ComputesMembers()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvSet("signup", "pro-user", "2026-03-01T10:00:00Z", new { plan = "pro" }),
            EvSet("signup", "free-user", "2026-03-01T11:00:00Z", new { plan = "free" }));

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Pro plan",
                type = "dynamic",
                rules = new[] { new { kind = "property", property = "plan", @operator = "equals", value = "pro" } },
            });

        var members = await GetAsync<CohortMembersResponse>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons");

        Assert.Equal(1, members.Count);
        Assert.Equal(await PersonIdAsync(projectId, "pro-user"), members.PersonIds.Single());
    }

    [Fact]
    public async Task DynamicCohort_BehavioralRule_UsesLookbackWindow()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        var recent = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
        var stale = DateTimeOffset.UtcNow.AddDays(-40).ToString("O");
        await CaptureBatchAsync(apiKey,
            Ev("purchase", "recent-buyer", recent),
            Ev("purchase", "lapsed-buyer", stale));

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Recent buyers",
                type = "dynamic",
                rules = new[] { new { kind = "performed_event", @event = "purchase", days = 30 } },
            });

        var members = await GetAsync<CohortMembersResponse>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons");

        Assert.Equal(1, members.Count);
        Assert.Equal(await PersonIdAsync(projectId, "recent-buyer"), members.PersonIds.Single());
    }

    [Fact]
    public async Task DynamicCohort_MultipleRules_IntersectWithAnd()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        await CaptureBatchAsync(apiKey,
            // Pro plan AND recently purchased.
            EvSet("signup", "match", recent, new { plan = "pro" }),
            Ev("purchase", "match", recent),
            // Pro plan but never purchased.
            EvSet("signup", "no-purchase", recent, new { plan = "pro" }),
            // Purchased but on the free plan.
            EvSet("signup", "free", recent, new { plan = "free" }),
            Ev("purchase", "free", recent));

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Paying pros",
                type = "dynamic",
                rules = new object[]
                {
                    new { kind = "property", property = "plan", @operator = "equals", value = "pro" },
                    new { kind = "performed_event", @event = "purchase", days = 7 },
                },
            });

        var members = await GetAsync<CohortMembersResponse>(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons");

        Assert.Equal(1, members.Count);
        Assert.Equal(await PersonIdAsync(projectId, "match"), members.PersonIds.Single());
    }

    [Fact]
    public async Task DynamicCohort_InvalidRules_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Broken",
                type = "dynamic",
                rules = new[] { new { kind = "teleport" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DynamicCohort_MembersCannotBeEditedByHand()
    {
        var (projectId, _) = await CreateProjectAsync();
        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Rules only",
                type = "dynamic",
                rules = new[] { new { kind = "performed_event", @event = "x" } },
            });

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/cohorts/{cohort.Id}/persons",
            new { personIds = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Cohorts as query filters ----------------------------------------------

    [Fact]
    public async Task Trend_WithCohortFilter_CountsOnlyMembers()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "in-cohort", "2026-03-02T10:00:00Z"),
            Ev("pageview", "in-cohort", "2026-03-02T11:00:00Z"),
            Ev("pageview", "outsider", "2026-03-02T12:00:00Z"));

        var personId = await PersonIdAsync(projectId, "in-cohort");
        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new { name = "VIPs", type = "static", personIds = new[] { personId } });

        var filters = $$"""[{"type":"cohort","value":"{{cohort.Id}}"}]""";
        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            $"&filters={Uri.EscapeDataString(filters)}");

        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(2, bucket.GetProperty("count").GetInt32());
        Assert.Equal(1, bucket.GetProperty("uniquePersons").GetInt32());
    }

    [Fact]
    public async Task Funnel_WithDynamicCohortFilter_RestrictsPersons()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvSet("signup", "pro", "2026-03-01T10:00:00Z", new { plan = "pro" }),
            Ev("activate", "pro", "2026-03-02T10:00:00Z"),
            EvSet("signup", "free", "2026-03-01T11:00:00Z", new { plan = "free" }),
            Ev("activate", "free", "2026-03-02T11:00:00Z"));

        var cohort = await PostAsync<CohortResponse>(
            $"/api/projects/{projectId}/cohorts",
            new
            {
                name = "Pros",
                type = "dynamic",
                rules = new[] { new { kind = "property", property = "plan", @operator = "equals", value = "pro" } },
            });

        var funnel = await PostAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/funnel",
            new
            {
                steps = new[] { "signup", "activate" },
                from = "2026-03-01T00:00:00Z",
                to = "2026-03-10T00:00:00Z",
                filters = new[] { new { type = "cohort", value = cohort.Id.ToString() } },
            });

        var steps = funnel.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([1, 1], steps.Select(s => s.GetProperty("persons").GetInt32()));
    }

    [Fact]
    public async Task CohortFilter_FromAnotherProject_MatchesNothing()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("pageview", "u1", "2026-03-02T10:00:00Z"));

        var (otherProjectId, otherKey) = await CreateProjectAsync();
        await CaptureBatchAsync(otherKey, Ev("pageview", "u1", "2026-03-02T10:00:00Z"));
        var foreignPerson = await PersonIdAsync(otherProjectId, "u1");
        var foreignCohort = await PostAsync<CohortResponse>(
            $"/api/projects/{otherProjectId}/cohorts",
            new { name = "Foreign", type = "static", personIds = new[] { foreignPerson } });

        var filters = $$"""[{"type":"cohort","value":"{{foreignCohort.Id}}"}]""";
        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day" +
            $"&filters={Uri.EscapeDataString(filters)}");

        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(0, bucket.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CohortFilter_WithNonGuidValue_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var filters = """[{"type":"cohort","value":"not-a-guid"}]""";
        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/insights/trend?event=x" +
            $"&filters={Uri.EscapeDataString(filters)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Helpers -----------------------------------------------------------------

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private static object EvSet(string name, string distinctId, string timestamp, object set) =>
        new
        {
            @event = name,
            distinct_id = distinctId,
            timestamp,
            properties = new Dictionary<string, object> { ["$set"] = set },
        };

    private async Task<Guid> PersonIdAsync(Guid projectId, string distinctId)
    {
        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/{distinctId}");
        return person.Id;
    }

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Cohorts {Guid.NewGuid():N}" });
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
