using System.Net;
using System.Net.Http.Json;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class ProjectEndpointsTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public ProjectEndpointsTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateProject_Returns201_WithGeneratedApiKey()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "My App" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var project = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        Assert.NotNull(project);
        Assert.Equal("My App", project.Name);
        Assert.StartsWith("pk_live_", project.ApiKey);
        Assert.NotEqual(Guid.Empty, project.Id);
    }

    [Fact]
    public async Task CreateProject_WithBlankName_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetProject_ById_ReturnsProject()
    {
        var created = await CreateAsync("Lookup Target");

        var fetched = await _client.GetFromJsonAsync<ProjectResponse>($"/api/projects/{created.Id}");

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.ApiKey, fetched.ApiKey);
    }

    [Fact]
    public async Task GetProject_UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListProjects_ContainsCreatedProjects()
    {
        var created = await CreateAsync("Listed Project");

        var projects = await _client.GetFromJsonAsync<List<ProjectResponse>>("/api/projects");

        Assert.NotNull(projects);
        Assert.Contains(projects, p => p.Id == created.Id);
    }

    private async Task<ProjectResponse> CreateAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }
}
