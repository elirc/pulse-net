using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

/// <summary>
/// Identity-merge edge cases: multi-hop identify chains, replayed merges,
/// property precedence across merges, and identify racing concurrent capture.
/// </summary>
public class IdentityChainTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public IdentityChainTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task IdentifyChain_AToBToC_CollapsesToOnePerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // device-a browses, becomes user-b, who is later aliased to user-c.
        await CaptureAsync(apiKey, Ev("pageview", "device-a"));
        await CaptureAsync(apiKey, Identify("user-b", anon: "device-a"));
        await CaptureAsync(apiKey, Identify("user-c", anon: "user-b"));
        await CaptureAsync(apiKey, Ev("purchase", "user-c"));

        Assert.Equal(1, CountPersons(projectId));

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/device-a");
        Assert.Equal(
            ["device-a", "user-b", "user-c"],
            person.DistinctIds.OrderBy(d => d, StringComparer.Ordinal).ToList());

        // Every event, from first pageview to final purchase, is on that person.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.All(
            db.Events.Where(e => e.ProjectId == projectId).ToList(),
            e => Assert.Equal(person.Id, e.PersonId));
    }

    [Fact]
    public async Task IdentifyReplay_AfterAFullMerge_ChangesNothing()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Two persons develop independently, then merge.
        await CaptureAsync(apiKey, EvSet("browse", "device-r", new { source = "ad" }));
        await CaptureAsync(apiKey, EvSet("signup", "user-r", new { plan = "pro" }));
        await CaptureAsync(apiKey, Identify("user-r", anon: "device-r"));

        var merged = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-r");

        // An SDK retry replays the same $identify (both ids now map to the
        // same person): still one person, same id, same properties.
        await CaptureAsync(apiKey, Identify("user-r", anon: "device-r"));

        Assert.Equal(1, CountPersons(projectId));
        var replayed = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-r");
        Assert.Equal(merged.Id, replayed.Id);
        Assert.Equal("pro", replayed.Properties.GetProperty("plan").GetString());
        Assert.Equal("ad", replayed.Properties.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Merge_WinnerPropertiesBeatLoserProperties_OnConflict()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // The anonymous (loser) person had plan=free; the identified (winner)
        // person has plan=pro. The winner's value survives the merge, and the
        // loser still contributes keys the winner lacks.
        await CaptureAsync(apiKey, EvSet("browse", "device-m", new { plan = "free", referrer = "google" }));
        await CaptureAsync(apiKey, EvSet("signup", "user-m", new { plan = "pro" }));
        await CaptureAsync(apiKey, Identify("user-m", anon: "device-m"));

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-m");

        Assert.Equal("pro", person.Properties.GetProperty("plan").GetString());
        Assert.Equal("google", person.Properties.GetProperty("referrer").GetString());
    }

    [Fact]
    public async Task SetAndSetOnce_SameKeyInOneEvent_SetWins()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureAsync(apiKey, new
        {
            @event = "signup",
            distinct_id = "user-so",
            properties = new Dictionary<string, object>
            {
                ["$set"] = new { plan = "pro" },
                ["$set_once"] = new { plan = "free" },
            },
        });

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-so");

        // $set applies first; $set_once only fills keys that are still absent.
        Assert.Equal("pro", person.Properties.GetProperty("plan").GetString());
    }

    [Fact]
    public async Task SetOnce_OnTheMergedPerson_StillRespectsExistingValues()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // The anonymous person locked in initial_referrer=google via $set_once.
        await CaptureAsync(apiKey, new
        {
            @event = "browse",
            distinct_id = "device-so",
            properties = new Dictionary<string, object>
            {
                ["$set_once"] = new { initial_referrer = "google" },
            },
        });
        await CaptureAsync(apiKey, Identify("user-so2", anon: "device-so"));

        // Post-merge, $set_once must not overwrite the inherited value.
        await CaptureAsync(apiKey, new
        {
            @event = "return-visit",
            distinct_id = "user-so2",
            properties = new Dictionary<string, object>
            {
                ["$set_once"] = new { initial_referrer = "bing" },
            },
        });

        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/user-so2");
        Assert.Equal("google", person.Properties.GetProperty("initial_referrer").GetString());
    }

    [Fact]
    public async Task Identify_RacingConcurrentCapture_LosesNoEventsAndKeepsOnePerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Seed the anonymous person, then fire an $identify batch and a burst
        // of anonymous events concurrently — no drain in between, so the
        // worker can interleave them in either order.
        await CaptureAsync(apiKey, Ev("pageview", "device-race"));

        var identify = _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[] { Identify("user-race", anon: "device-race") },
        });
        var burst = _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = Enumerable.Range(0, 10)
                .Select(i => (object)Ev($"click-{i}", "device-race"))
                .ToArray(),
        });

        var responses = await Task.WhenAll(identify, burst);
        Assert.All(responses, r => r.EnsureSuccessStatusCode());
        await TestIngestion.WaitForDrainAsync(_client);

        // However the interleave played out, everything funneled into one person.
        Assert.Equal(1, CountPersons(projectId));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var events = db.Events.Where(e => e.ProjectId == projectId).ToList();
        Assert.Equal(12, events.Count); // pageview + $identify + 10 clicks
        Assert.Single(events.Select(e => e.PersonId).Distinct());
    }

    // --- Helpers ---------------------------------------------------------------

    private static object Ev(string name, string distinctId) =>
        new { @event = name, distinct_id = distinctId };

    private static object EvSet(string name, string distinctId, object set) =>
        new
        {
            @event = name,
            distinct_id = distinctId,
            properties = new Dictionary<string, object> { ["$set"] = set },
        };

    private static object Identify(string distinctId, string anon) =>
        new
        {
            @event = "$identify",
            distinct_id = distinctId,
            properties = new Dictionary<string, object> { ["$anon_distinct_id"] = anon },
        };

    private int CountPersons(Guid projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return db.Persons.Count(p => p.ProjectId == projectId);
    }

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"IdentityChain {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey);
    }

    private async Task CaptureAsync(string apiKey, object payload)
    {
        var withKey = System.Text.Json.JsonSerializer.SerializeToNode(payload)!.AsObject();
        withKey["api_key"] = apiKey;
        var response = await _client.PostAsJsonAsync("/capture", withKey);
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
