using System.Net;
using System.Net.Http.Json;

namespace Pulse.Tests.Api;

public class HealthEndpointTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;

    public HealthEndpointTests(PulseApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsHealthyStatus()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(body);
        Assert.Equal("healthy", body.Status);
        Assert.Equal("pulse-net", body.Service);
    }

    private sealed record HealthResponse(string Status, string Service, DateTime Timestamp);
}
