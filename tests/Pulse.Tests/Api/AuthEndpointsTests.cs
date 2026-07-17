using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class AuthEndpointsTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointsTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Accounts & sessions ------------------------------------------------

    [Fact]
    public async Task Register_ReturnsTokenAndUser()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "Ada@Example.com",
            password = "hunter2hunter2",
            name = "Ada",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.Equal("ada@example.com", auth.User.Email); // Normalized lowercase.
        Assert.True(auth.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var (_, email) = await TestAuth.RegisterAsync(_client);

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "another-password",
            name = "Copycat",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_ShortPassword_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "short@test.dev",
            password = "abc",
            name = "Short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithCorrectPassword_ReturnsToken()
    {
        var (_, email) = await TestAuth.RegisterAsync(_client);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestAuth.Password,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var (_, email) = await TestAuth.RegisterAsync(_client);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "not-the-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithToken_ReturnsCurrentUser()
    {
        var client = _factory.CreateClient();
        var (token, email) = await TestAuth.RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal(email, me.Email);
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Personal API keys ----------------------------------------------------

    [Fact]
    public async Task PersonalApiKey_AuthenticatesTheManagementApi()
    {
        var client = await AuthenticatedClientAsync();

        var created = await PostAsync<PersonalApiKeyCreatedResponse>(
            client, "/api/personal-api-keys", new { name = "CI script" });
        Assert.StartsWith("pk_user_", created.Key);

        // A fresh client using only the personal key can call the management API.
        var keyClient = _factory.CreateClient();
        keyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.Key);

        var project = await PostAsync<ProjectResponse>(
            keyClient, "/api/projects", new { name = "Via personal key" });
        Assert.Equal("Via personal key", project.Name);

        // Listing never exposes the full key again.
        var listed = (await client.GetFromJsonAsync<List<PersonalApiKeyResponse>>(
            "/api/personal-api-keys"))!;
        Assert.Single(listed);
        Assert.Equal(created.Key[^4..], listed[0].KeySuffix);
    }

    [Fact]
    public async Task PersonalApiKey_AfterDeletion_StopsWorking()
    {
        var client = await AuthenticatedClientAsync();
        var created = await PostAsync<PersonalApiKeyCreatedResponse>(
            client, "/api/personal-api-keys", new { name = "Doomed" });

        var delete = await client.DeleteAsync($"/api/personal-api-keys/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var keyClient = _factory.CreateClient();
        keyClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.Key);
        var response = await keyClient.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Project membership & cross-project denial ---------------------------

    [Fact]
    public async Task NonMember_CannotSeeAnotherUsersProject()
    {
        var owner = await AuthenticatedClientAsync();
        var project = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Private" });

        var intruder = await AuthenticatedClientAsync();

        // Project lookup, insights, queries and persons all 404 for non-members.
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{project.Id}/insights")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{project.Id}/insights/trend?event=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await intruder.GetAsync($"/api/projects/{project.Id}/persons")).StatusCode);

        // And it never shows up in their project list.
        var list = (await intruder.GetFromJsonAsync<List<ProjectResponse>>("/api/projects"))!;
        Assert.DoesNotContain(list, p => p.Id == project.Id);
    }

    [Fact]
    public async Task AddedMember_GainsAccess()
    {
        var owner = await AuthenticatedClientAsync();
        var project = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Shared" });

        var invitee = _factory.CreateClient();
        var (token, email) = await TestAuth.RegisterAsync(invitee);
        invitee.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.NotFound,
            (await invitee.GetAsync($"/api/projects/{project.Id}")).StatusCode);

        var add = await owner.PostAsJsonAsync($"/api/projects/{project.Id}/members", new { email });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var fetched = await invitee.GetFromJsonAsync<ProjectResponse>($"/api/projects/{project.Id}");
        Assert.Equal(project.Id, fetched!.Id);

        var members = (await owner.GetFromJsonAsync<List<MemberResponse>>(
            $"/api/projects/{project.Id}/members"))!;
        Assert.Equal(2, members.Count);
    }

    // --- Read vs write key semantics ------------------------------------------

    [Fact]
    public async Task ReadKey_GrantsQueryAccess_WithoutAUser()
    {
        var owner = await AuthenticatedClientAsync();
        var project = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Embedded" });
        await CaptureAsync(project.ApiKey, "pageview", "u1");

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", project.ReadKey);

        var response = await anonymous.GetAsync(
            $"/api/projects/{project.Id}/insights/trend?event=pageview&interval=day");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var trend = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, trend.GetProperty("buckets").EnumerateArray()
            .Sum(b => b.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task WriteKey_IsRejectedForQueries()
    {
        var owner = await AuthenticatedClientAsync();
        var project = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Write only" });

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", project.ApiKey);

        var response = await anonymous.GetAsync(
            $"/api/projects/{project.Id}/insights/trend?event=pageview&interval=day");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadKey_IsRejectedForCapture()
    {
        var owner = await AuthenticatedClientAsync();
        var project = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Read only" });

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = project.ReadKey,
            @event = "sneaky",
            distinct_id = "u1",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadKey_ForTheWrongProject_IsRejected()
    {
        var owner = await AuthenticatedClientAsync();
        var mine = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Mine" });
        var other = await PostAsync<ProjectResponse>(owner, "/api/projects", new { name = "Other" });

        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", other.ReadKey);

        var response = await anonymous.GetAsync(
            $"/api/projects/{mine.Id}/insights/trend?event=pageview&interval=day");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ---------------------------------------------------------------

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        return client;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object payload)
    {
        var response = await client.PostAsJsonAsync(url, payload);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task CaptureAsync(string apiKey, string eventName, string distinctId)
    {
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            @event = eventName,
            distinct_id = distinctId,
        });
        response.EnsureSuccessStatusCode();
    }
}
