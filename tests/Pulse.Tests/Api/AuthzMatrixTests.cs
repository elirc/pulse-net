using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

/// <summary>
/// The full authorization matrix: membership × endpoint class, and
/// write-key / read-key / personal-key behavior on each surface. Non-members
/// must always see 404 (never 403), and the wrong key type must never grant
/// access.
/// </summary>
public class AuthzMatrixTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public AuthzMatrixTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>One representative route per project-scoped endpoint class.</summary>
    public static TheoryData<string, string, string?> ProjectEndpoints() => new()
    {
        // method, path template ({0} = projectId), JSON body for POSTs
        { "GET", "/api/projects/{0}", null },
        { "GET", "/api/projects/{0}/insights/trend?event=x", null },
        { "POST", "/api/projects/{0}/insights/funnel", """{"steps":["a","b"]}""" },
        { "GET", "/api/projects/{0}/insights/retention", null },
        { "GET", "/api/projects/{0}/insights", null },
        { "POST", "/api/projects/{0}/insights", """{"name":"n","type":"trend"}""" },
        { "GET", "/api/projects/{0}/persons", null },
        { "GET", "/api/projects/{0}/cohorts", null },
        { "POST", "/api/projects/{0}/cohorts", """{"name":"c","type":"static"}""" },
        { "GET", "/api/projects/{0}/feature-flags", null },
        { "POST", "/api/projects/{0}/feature-flags", """{"key":"k","type":"boolean"}""" },
        { "GET", "/api/projects/{0}/dashboards", null },
        { "POST", "/api/projects/{0}/dashboards", """{"name":"d"}""" },
        { "GET", "/api/projects/{0}/annotations", null },
        { "GET", "/api/projects/{0}/event-definitions", null },
        { "GET", "/api/projects/{0}/property-definitions", null },
        { "GET", "/api/projects/{0}/export/events", null },
        { "GET", "/api/projects/{0}/export/persons", null },
        { "POST", "/api/projects/{0}/exports", """{"type":"events","format":"json"}""" },
        { "GET", "/api/projects/{0}/ingestion/dead-letters", null },
    };

    [Theory]
    [MemberData(nameof(ProjectEndpoints))]
    public async Task NonMember_Gets404_NeverA403(string method, string pathTemplate, string? body)
    {
        var projectId = await CreateProjectAsync();

        // A different, fully authenticated user who is not a member.
        var intruder = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(intruder);

        var response = await SendAsync(intruder, method, string.Format(pathTemplate, projectId), body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProjectEndpoints))]
    public async Task Unauthenticated_Gets401(string method, string pathTemplate, string? body)
    {
        var projectId = await CreateProjectAsync();

        var anonymous = _factory.CreateClient();
        var response = await SendAsync(anonymous, method, string.Format(pathTemplate, projectId), body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Read key ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/projects/{0}/insights/trend?event=x", null)]
    [InlineData("POST", "/api/projects/{0}/insights/funnel", """{"steps":["a","b"]}""")]
    [InlineData("GET", "/api/projects/{0}/insights/retention", null)]
    public async Task ReadKey_GrantsQueryEndpoints(string method, string pathTemplate, string? body)
    {
        var (projectId, _, readKey) = await CreateProjectWithKeysAsync();

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", readKey);

        var response = await SendAsync(anonymous, method, string.Format(pathTemplate, projectId), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/projects/{0}/persons", null)]
    [InlineData("GET", "/api/projects/{0}/export/events", null)]
    [InlineData("POST", "/api/projects/{0}/feature-flags", """{"key":"k","type":"boolean"}""")]
    [InlineData("GET", "/api/projects/{0}/ingestion/dead-letters", null)]
    public async Task ReadKey_DoesNotGrantManagementEndpoints(string method, string pathTemplate, string? body)
    {
        var (projectId, _, readKey) = await CreateProjectWithKeysAsync();

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", readKey);

        var response = await SendAsync(anonymous, method, string.Format(pathTemplate, projectId), body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WriteKey_DoesNotGrantQueryEndpoints()
    {
        var (projectId, apiKey, _) = await CreateProjectWithKeysAsync();

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var response = await anonymous.GetAsync(
            $"/api/projects/{projectId}/insights/trend?event=x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadKey_OfAnotherProject_DoesNotCross()
    {
        var (projectA, _, _) = await CreateProjectWithKeysAsync();
        var (_, _, readKeyB) = await CreateProjectWithKeysAsync();

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", readKeyB);

        var response = await anonymous.GetAsync(
            $"/api/projects/{projectA}/insights/trend?event=x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadKey_DoesNotAuthenticateCapture()
    {
        var (_, _, readKey) = await CreateProjectWithKeysAsync();

        var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/capture", new
        {
            api_key = readKey,
            @event = "x",
            distinct_id = "u1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Personal keys -----------------------------------------------------------------

    [Fact]
    public async Task PersonalKey_GrantsTheSameProjectsAsTheOwningUser()
    {
        var projectId = await CreateProjectAsync();

        var created = await PostAsync<PersonalApiKeyCreatedResponse>(
            "/api/personal-api-keys", new { name = "ci" });

        var scripted = _factory.CreateClient();
        scripted.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.Key);

        // Management read...
        var projects = await scripted.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");
        Assert.Contains(projects!, p => p.Id == projectId);

        // ...query access...
        var trend = await scripted.GetAsync(
            $"/api/projects/{projectId}/insights/trend?event=x");
        Assert.Equal(HttpStatusCode.OK, trend.StatusCode);

        // ...and management writes.
        var flag = await scripted.PostAsJsonAsync(
            $"/api/projects/{projectId}/feature-flags",
            new { key = "from-script", type = "boolean" });
        Assert.Equal(HttpStatusCode.Created, flag.StatusCode);
    }

    [Fact]
    public async Task PersonalKey_OfANonMember_StillGets404()
    {
        var projectId = await CreateProjectAsync();

        var intruder = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(intruder);
        var created = await PostAsync<PersonalApiKeyCreatedResponse>(
            intruder, "/api/personal-api-keys", new { name = "intruder-key" });

        var scripted = _factory.CreateClient();
        scripted.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.Key);

        var response = await scripted.GetAsync($"/api/projects/{projectId}/persons");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RevokedPersonalKey_StopsWorkingImmediately()
    {
        await CreateProjectAsync();

        var created = await PostAsync<PersonalApiKeyCreatedResponse>(
            "/api/personal-api-keys", new { name = "revoked" });

        var scripted = _factory.CreateClient();
        scripted.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.Key);
        var before = await scripted.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var revoke = await _client.DeleteAsync($"/api/personal-api-keys/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var after = await scripted.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    // --- Helpers ---------------------------------------------------------------

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string url, string? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Parse(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private async Task<Guid> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Authz {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
    }

    private async Task<(Guid ProjectId, string ApiKey, string ReadKey)> CreateProjectWithKeysAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Authz {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
        return (project.Id, project.ApiKey, project.ReadKey);
    }

    private Task<T> PostAsync<T>(string url, object payload) => PostAsync<T>(_client, url, payload);

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
