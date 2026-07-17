using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class IdentityTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public IdentityTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Capture_UnseenDistinctId_CreatesPerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureAsync(apiKey, new { @event = "pageview", distinct_id = "anon-new" });

        var person = await GetPersonByDistinctIdAsync(projectId, "anon-new");
        Assert.NotNull(person);
        Assert.Contains("anon-new", person.DistinctIds);
    }

    [Fact]
    public async Task Capture_SameDistinctIdTwice_ReusesPerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureAsync(apiKey, new { @event = "a", distinct_id = "repeat-user" });
        await CaptureAsync(apiKey, new { @event = "b", distinct_id = "repeat-user" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var personIds = db.Events
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.PersonId)
            .Distinct()
            .ToList();
        Assert.Single(personIds);
    }

    [Fact]
    public async Task Identify_LinksAnonEventsToIdentifiedPerson()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Anonymous browsing, then login triggers $identify.
        await CaptureAsync(apiKey, new { @event = "pageview", distinct_id = "device-42" });
        await CaptureAsync(apiKey, new
        {
            @event = "$identify",
            distinct_id = "user-alice",
            properties = new Dictionary<string, object> { ["$anon_distinct_id"] = "device-42" },
        });
        await CaptureAsync(apiKey, new { @event = "purchase", distinct_id = "user-alice" });

        var person = await GetPersonByDistinctIdAsync(projectId, "user-alice");
        Assert.NotNull(person);
        Assert.Equal(["device-42", "user-alice"], person.DistinctIds.OrderBy(d => d).ToList());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var personIds = db.Events
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.PersonId)
            .Distinct()
            .ToList();
        Assert.Single(personIds);
        Assert.Equal(person.Id, personIds[0]);
    }

    [Fact]
    public async Task Identify_MergesTwoExistingPersons_AndRepointsEvents()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        // Two separate persons develop independently...
        await CaptureAsync(apiKey, new { @event = "anon-browse", distinct_id = "device-7" });
        await CaptureAsync(apiKey, new
        {
            @event = "signup",
            distinct_id = "user-bob",
            properties = new Dictionary<string, object>
            {
                ["$set"] = new Dictionary<string, object> { ["email"] = "bob@example.com" },
            },
        });

        Assert.Equal(2, await CountPersonsAsync(projectId));

        // ...then $identify reveals they are the same human.
        await CaptureAsync(apiKey, new
        {
            @event = "$identify",
            distinct_id = "user-bob",
            properties = new Dictionary<string, object> { ["$anon_distinct_id"] = "device-7" },
        });

        Assert.Equal(1, await CountPersonsAsync(projectId));

        var person = await GetPersonByDistinctIdAsync(projectId, "device-7");
        Assert.NotNull(person);
        Assert.Equal("bob@example.com", person.Properties.GetProperty("email").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.All(
            db.Events.Where(e => e.ProjectId == projectId).ToList(),
            e => Assert.Equal(person.Id, e.PersonId));
    }

    [Fact]
    public async Task Identify_IsIdempotent()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        for (var i = 0; i < 2; i++)
        {
            await CaptureAsync(apiKey, new
            {
                @event = "$identify",
                distinct_id = "user-carol",
                properties = new Dictionary<string, object> { ["$anon_distinct_id"] = "device-c" },
            });
        }

        Assert.Equal(1, await CountPersonsAsync(projectId));
    }

    [Fact]
    public async Task Set_UpdatesPersonProperties_AndSetOncePreservesFirstValue()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureAsync(apiKey, new
        {
            @event = "signup",
            distinct_id = "user-dave",
            properties = new Dictionary<string, object>
            {
                ["$set"] = new Dictionary<string, object> { ["plan"] = "free" },
                ["$set_once"] = new Dictionary<string, object> { ["initial_referrer"] = "google" },
            },
        });
        await CaptureAsync(apiKey, new
        {
            @event = "upgrade",
            distinct_id = "user-dave",
            properties = new Dictionary<string, object>
            {
                ["$set"] = new Dictionary<string, object> { ["plan"] = "pro" },
                ["$set_once"] = new Dictionary<string, object> { ["initial_referrer"] = "bing" },
            },
        });

        var person = await GetPersonByDistinctIdAsync(projectId, "user-dave");
        Assert.NotNull(person);
        Assert.Equal("pro", person.Properties.GetProperty("plan").GetString());
        Assert.Equal("google", person.Properties.GetProperty("initial_referrer").GetString());
    }

    [Fact]
    public async Task IdentifyThenAnonEvent_ResolvesToSamePerson_InSingleBatch()
    {
        var (projectId, apiKey) = await CreateProjectAsync();

        await CaptureAsync(apiKey, new
        {
            batch = new object[]
            {
                new { @event = "pageview", distinct_id = "device-batch" },
                new
                {
                    @event = "$identify",
                    distinct_id = "user-batch",
                    properties = new Dictionary<string, object> { ["$anon_distinct_id"] = "device-batch" },
                },
                new { @event = "checkout", distinct_id = "user-batch" },
            },
        });

        Assert.Equal(1, await CountPersonsAsync(projectId));
    }

    [Fact]
    public async Task PersonsList_ReturnsPersonsWithDistinctIds()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureAsync(apiKey, new { @event = "e", distinct_id = "list-user-1" });
        await CaptureAsync(apiKey, new { @event = "e", distinct_id = "list-user-2" });

        var persons = await _client.GetFromJsonAsync<List<PersonResponse>>(
            $"/api/projects/{projectId}/persons");

        Assert.NotNull(persons);
        Assert.Equal(2, persons.Count);
        Assert.Contains(persons, p => p.DistinctIds.Contains("list-user-1"));
    }

    [Fact]
    public async Task GetPerson_UnknownId_Returns404()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{projectId}/persons/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<int> CountPersonsAsync(Guid projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        return Task.FromResult(db.Persons.Count(p => p.ProjectId == projectId));
    }

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Identity {Guid.NewGuid():N}" });
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
    }

    private async Task<PersonResponse?> GetPersonByDistinctIdAsync(Guid projectId, string distinctId)
    {
        var response = await _client.GetAsync(
            $"/api/projects/{projectId}/persons/by-distinct-id/{distinctId}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PersonResponse>();
    }
}
