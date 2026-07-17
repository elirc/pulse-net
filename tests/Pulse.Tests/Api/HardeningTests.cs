using System.Net;
using System.Net.Http.Json;
using System.Text;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class HardeningTests : IClassFixture<PulseApiFactory>
{
    private readonly HttpClient _client;

    public HardeningTests(PulseApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Capture_MalformedJson_Returns400NotServerError()
    {
        var response = await _client.PostAsync(
            "/capture",
            new StringContent("{not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Capture_InvalidKey_UsesProblemDetailsContentType()
    {
        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = "pk_live_ffffffffffffffffffffffffffffffff",
            @event = "x",
            distinct_id = "y",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ValidationFailure_ReturnsFieldLevelErrors()
    {
        await TestAuth.AuthenticateAsync(_client);
        var project = await _client.PostAsJsonAsync("/api/projects", new { name = "Hardening" });
        var apiKey = (await project.Content.ReadFromJsonAsync<ProjectResponse>())!.ApiKey;

        var response = await _client.PostAsJsonAsync("/capture", new
        {
            api_key = apiKey,
            batch = new object[]
            {
                new { @event = "ok", distinct_id = "u1" },
                new { @event = "", distinct_id = "" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("batch[1].event", body);
        Assert.Contains("batch[1].distinct_id", body);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await _client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
