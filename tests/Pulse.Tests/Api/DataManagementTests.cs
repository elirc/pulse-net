using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class DataManagementTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public DataManagementTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Annotations -----------------------------------------------------------

    [Fact]
    public async Task Annotations_CrudRoundTrips()
    {
        var (projectId, _) = await CreateProjectAsync();

        var created = await PostAsync<AnnotationResponse>(
            $"/api/projects/{projectId}/annotations",
            new { date = "2026-03-02", content = "v2.0 released" });
        Assert.Equal(new DateOnly(2026, 3, 2), created.Date);

        var updated = await PutAsync<AnnotationResponse>(
            $"/api/projects/{projectId}/annotations/{created.Id}",
            new { content = "v2.0 released to 100%" });
        Assert.Equal("v2.0 released to 100%", updated.Content);

        var list = await GetAsync<List<AnnotationResponse>>(
            $"/api/projects/{projectId}/annotations");
        Assert.Single(list);

        // Date-range filtering.
        var outside = await GetAsync<List<AnnotationResponse>>(
            $"/api/projects/{projectId}/annotations?from=2026-04-01");
        Assert.Empty(outside);

        var delete = await _client.DeleteAsync(
            $"/api/projects/{projectId}/annotations/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Annotations_AppearOnTrends_OnlyWithinRange()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "u1", "2026-03-02T10:00:00Z"));

        await PostAsync<AnnotationResponse>(
            $"/api/projects/{projectId}/annotations",
            new { date = "2026-03-02", content = "Launch day" });
        await PostAsync<AnnotationResponse>(
            $"/api/projects/{projectId}/annotations",
            new { date = "2026-05-20", content = "Way in the future" });

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-01T00:00:00Z&to=2026-03-04T23:59:59Z&interval=day");

        var annotations = trend.GetProperty("annotations").EnumerateArray().ToList();
        var annotation = Assert.Single(annotations);
        Assert.Equal("Launch day", annotation.GetProperty("content").GetString());
        Assert.Equal("2026-03-02", annotation.GetProperty("date").GetString());
    }

    // --- Definitions registry ----------------------------------------------------

    [Fact]
    public async Task Definitions_AutoPopulateOnIngest()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvProps("pageview", "u1", new { url = "/pricing", duration = 12.5 }),
            EvProps("pageview", "u2", new { url = "/docs", logged_in = true }),
            EvProps("purchase", "u1", new Dictionary<string, object>
            {
                ["amount"] = 42,
                ["$set"] = new { plan = "pro" }, // system key: excluded
            }));

        var events = await GetAsync<List<EventDefinitionResponse>>(
            $"/api/projects/{projectId}/event-definitions");
        Assert.Equal(["pageview", "purchase"], events.Select(d => d.Name));

        var properties = await GetAsync<List<PropertyDefinitionResponse>>(
            $"/api/projects/{projectId}/property-definitions");
        Assert.Equal(
            ["amount", "duration", "logged_in", "url"],
            properties.Select(d => d.Name));
        Assert.Equal("number", properties.Single(d => d.Name == "amount").PropertyType);
        Assert.Equal("number", properties.Single(d => d.Name == "duration").PropertyType);
        Assert.Equal("boolean", properties.Single(d => d.Name == "logged_in").PropertyType);
        Assert.Equal("string", properties.Single(d => d.Name == "url").PropertyType);
    }

    [Fact]
    public async Task Definitions_AreIdempotentAcrossRepeatedEvents()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            EvProps("login", "u1", new { device = "ios" }),
            EvProps("login", "u1", new { device = "android" }),
            EvProps("login", "u2", new { device = "web" }));

        var events = await GetAsync<List<EventDefinitionResponse>>(
            $"/api/projects/{projectId}/event-definitions");
        Assert.Single(events);

        var properties = await GetAsync<List<PropertyDefinitionResponse>>(
            $"/api/projects/{projectId}/property-definitions");
        Assert.Single(properties);
    }

    // --- Person deletion (GDPR) ---------------------------------------------------

    [Fact]
    public async Task DeletePerson_PurgesPersonEventsAndMappings()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey,
            Ev("pageview", "doomed", "2026-03-02T10:00:00Z"),
            Ev("purchase", "doomed", "2026-03-02T11:00:00Z"),
            Ev("pageview", "survivor", "2026-03-02T12:00:00Z"));

        var doomed = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/doomed");

        var response = await _client.DeleteAsync(
            $"/api/projects/{projectId}/persons/{doomed.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<PersonDeletionResponse>())!;
        Assert.Equal(2, receipt.DeletedEvents);
        Assert.Equal(1, receipt.DeletedDistinctIds);

        // Person, mapping and events are gone; other persons untouched.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/projects/{projectId}/persons/{doomed.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync(
                $"/api/projects/{projectId}/persons/by-distinct-id/doomed")).StatusCode);

        var trend = await GetAsync<JsonElement>(
            $"/api/projects/{projectId}/insights/trend?event=pageview" +
            "&from=2026-03-02T00:00:00Z&to=2026-03-02T23:59:59Z&interval=day");
        var bucket = trend.GetProperty("buckets").EnumerateArray().Single();
        Assert.Equal(1, bucket.GetProperty("count").GetInt32()); // survivor only
    }

    [Fact]
    public async Task DeletePerson_UnknownPerson_Returns404()
    {
        var (projectId, _) = await CreateProjectAsync();

        var response = await _client.DeleteAsync(
            $"/api/projects/{projectId}/persons/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletePerson_ByNonMember_Returns404()
    {
        var (projectId, apiKey) = await CreateProjectAsync();
        await CaptureBatchAsync(apiKey, Ev("visit", "victim", "2026-03-02T10:00:00Z"));
        var person = await GetAsync<PersonResponse>(
            $"/api/projects/{projectId}/persons/by-distinct-id/victim");

        var intruder = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(intruder);

        var response = await intruder.DeleteAsync(
            $"/api/projects/{projectId}/persons/{person.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers -------------------------------------------------------------------

    private static object Ev(string name, string distinctId, string timestamp) =>
        new { @event = name, distinct_id = distinctId, timestamp };

    private static object EvProps(string name, string distinctId, object properties) =>
        new { @event = name, distinct_id = distinctId, properties };

    private async Task<(Guid ProjectId, string ApiKey)> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"DataMgmt {Guid.NewGuid():N}" });
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

    private async Task<T> PutAsync<T>(string url, object payload)
    {
        var response = await _client.PutAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
