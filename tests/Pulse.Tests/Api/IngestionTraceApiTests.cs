using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulse.Api.Ingestion;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class IngestionTraceApiTests
{
    [Fact]
    public async Task ResponseHeaderMatchesPersistedBatchContext_AndValidationFailuresStillHaveCorrelation()
    {
        await using var parent = new PulseApiFactory();
        using var factory = parent.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(IngestionWorker)))));
        using var client = factory.CreateClient(); await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Trace headers" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/capture")
        {
            Content = JsonContent.Create(new { api_key = project.ApiKey, batch = new[] { new { @event = "e", distinct_id = "private-marker" }, new { @event = "e", distinct_id = "other-marker" } } })
        };
        request.Headers.Add("traceparent", "invalid-untrusted-context");
        var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var header = Assert.Single(response.Headers.GetValues("X-Trace-Id")); Assert.Equal(32, header.Length);
        Assert.NotEqual(default, ActivityTraceId.CreateFromString(header));
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var contexts = await db.QueuedEvents.Where(q => q.ProjectId == project.Id).Select(q => q.TraceParent).ToListAsync();
        Assert.Equal(2, contexts.Count); Assert.Single(contexts.Distinct());
        Assert.All(contexts, context => { Assert.True(IngestionTrace.TryParse(context, out var parsed)); Assert.Equal(header, parsed.TraceId.ToString()); Assert.DoesNotContain("marker", context!); });
        var invalid = await client.PostAsJsonAsync("/capture", new { }); Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(32, Assert.Single(invalid.Headers.GetValues("X-Trace-Id")).Length);
    }
}
