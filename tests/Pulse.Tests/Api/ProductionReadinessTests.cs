using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Pulse.Api.Contracts;

namespace Pulse.Tests.Api;

public class ProductionReadinessTests : IClassFixture<PulseApiFactory>
{
    private readonly PulseApiFactory _factory;
    private readonly HttpClient _client;

    public ProductionReadinessTests(PulseApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Health probes ----------------------------------------------------------

    [Fact]
    public async Task Health_IncludesDatabaseAndQueueProbes()
    {
        var health = await _client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.Equal("pulse-net", health.GetProperty("service").GetString());

        var checks = health.GetProperty("checks");
        Assert.Equal("ok", checks.GetProperty("database").GetString());
        Assert.True(checks.GetProperty("queue").GetProperty("pending").GetInt32() >= 0);
        Assert.True(checks.GetProperty("queue").GetProperty("deadLetters").GetInt32() >= 0);
    }

    // --- Rate limiting on capture --------------------------------------------------

    [Fact]
    public async Task Capture_OverTheRateLimit_Returns429()
    {
        using var limitedFactory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Capture:PermitLimit", "3"));
        using var client = limitedFactory.CreateClient();

        var payload = new
        {
            api_key = "pk_live_ffffffffffffffffffffffffffffffff",
            @event = "x",
            distinct_id = "u1",
        };

        // The first three requests reach the endpoint (401: unknown key)...
        for (var i = 0; i < 3; i++)
        {
            var allowed = await client.PostAsJsonAsync("/capture", payload);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        // ...the fourth is rejected by the limiter before the endpoint runs.
        var limited = await client.PostAsJsonAsync("/capture", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Other endpoints are not rate limited.
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // --- Pagination -----------------------------------------------------------------

    [Fact]
    public async Task InsightList_HonorsLimitAndOffset()
    {
        var projectId = await CreateProjectAsync();
        for (var i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync(
                $"/api/projects/{projectId}/insights",
                new { name = $"Insight {i}", type = "trend", config = new { @event = "x" } });
            response.EnsureSuccessStatusCode();
        }

        var firstPage = await _client.GetFromJsonAsync<List<InsightResponse>>(
            $"/api/projects/{projectId}/insights?limit=2");
        Assert.Equal(2, firstPage!.Count);
        Assert.Equal("Insight 0", firstPage[0].Name);

        var secondPage = await _client.GetFromJsonAsync<List<InsightResponse>>(
            $"/api/projects/{projectId}/insights?limit=2&offset=2");
        var last = Assert.Single(secondPage!);
        Assert.Equal("Insight 2", last.Name);
    }

    [Fact]
    public async Task ProjectList_HonorsLimitAndOffset()
    {
        // Fresh user so the list is exactly ours.
        var client = _factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/projects", new { name = $"P{i}" });
            response.EnsureSuccessStatusCode();
        }

        var page = await client.GetFromJsonAsync<List<ProjectResponse>>(
            "/api/projects?limit=1&offset=1");
        var project = Assert.Single(page!);
        Assert.Equal("P1", project.Name);
    }

    // --- Request logging ---------------------------------------------------------------

    [Fact]
    public async Task Requests_EmitOneStructuredLogLine()
    {
        var provider = new CapturingLoggerProvider();
        using var loggedFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(provider)));
        using var client = loggedFactory.CreateClient();

        await client.GetAsync("/api/ingestion/metrics");

        Assert.Contains(provider.Entries, entry =>
            entry.Contains("HTTP GET /api/ingestion/metrics responded 200"));

        // Health probes are excluded from request logging.
        provider.Entries.Clear();
        await client.GetAsync("/health");
        Assert.DoesNotContain(provider.Entries, entry => entry.Contains("/health"));
    }

    // --- Helpers -------------------------------------------------------------------------

    private async Task<Guid> CreateProjectAsync()
    {
        await TestAuth.AuthenticateAsync(_client);
        var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name = $"Prod {Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!.Id;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentBag<string> _entries;

            public CapturingLogger(ConcurrentBag<string> entries)
            {
                _entries = entries;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _entries.Add(formatter(state, exception));
        }
    }
}
