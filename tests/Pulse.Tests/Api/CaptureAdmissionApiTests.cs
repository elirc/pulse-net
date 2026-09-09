using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulse.Api.Contracts;
using Pulse.Api.Ingestion;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class CaptureAdmissionApiTests
{
    private static async Task<ProjectResponse> SetupAsync(HttpClient client)
    {
        await TestAuth.AuthenticateAsync(client);
        return (await (await client.PostAsJsonAsync("/api/projects", new { name = "Admission API" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
    }

    [Fact]
    public async Task IdempotencyReceiptsAndCapacity_JoinRealHttpAdmissionAndProcessing()
    {
        await using var isolated = new PulseApiFactory();
        await using var server = isolated.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(IngestionWorker)))));
        using var client = server.CreateClient(); var project = await SetupAsync(client); var root = $"/api/projects/{project.Id}";
        var limits = await client.PutAsJsonAsync(root + "/ingestion/limits", new { maxPending = 1 }); limits.EnsureSuccessStatusCode();
        var id = Guid.NewGuid(); var body = new { api_key = project.ApiKey, event_id = id, @event = " signup ", distinct_id = " person ", properties = new { plan = "pro" } };
        var first = await client.PostAsJsonAsync("/capture", body); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());
        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, "/capture") { Content = JsonContent.Create(body) };
        retryRequest.Headers.Add("X-Capture-Receipt", "true"); var retried = await client.SendAsync(retryRequest); Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        var result = await retried.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(0, result.GetProperty("queued").GetInt32()); Assert.Equal(1, result.GetProperty("deduplicated").GetInt32());
        var url = result.GetProperty("statusUrl").GetString()!;
        var receipt = await client.GetFromJsonAsync<CaptureReceiptStatus>(url); Assert.Equal(1, receipt!.Queued);
        var blocked = await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, @event = "other", distinct_id = "person" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); Assert.Equal(TimeSpan.FromSeconds(1), blocked.Headers.RetryAfter!.Delta);
        Assert.Equal("queue_capacity_exceeded", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var conflict = await client.PostAsJsonAsync("/capture", new { api_key = project.ApiKey, event_id = id, @event = "changed", distinct_id = "person" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var anonymous = server.CreateClient(); anonymous.DefaultRequestHeaders.Add("X-Api-Key", project.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        await TestAuth.AuthenticateAsync(anonymous); Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        using (var scope = server.Services.CreateScope()) Assert.Equal((1, 0), await scope.ServiceProvider.GetRequiredService<IngestionProcessor>().ProcessPendingAsync());
        receipt = await client.GetFromJsonAsync<CaptureReceiptStatus>(url); Assert.Equal(1, receipt!.Processed); Assert.NotNull(receipt.CompletedAt); Assert.NotNull(receipt.Items[0].EventId);
        var raw = await client.GetStringAsync(url); Assert.DoesNotContain("plan", raw); Assert.DoesNotContain(project.ApiKey, raw); Assert.DoesNotContain("person", raw);
        using var verify = server.Services.CreateScope(); var db = verify.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(1, await db.Events.CountAsync()); Assert.Equal(0, await db.QueuedEvents.CountAsync());
    }

    [Theory]
    [InlineData("bad-id")]
    [InlineData("empty-id")]
    [InlineData("duplicate-top-level")]
    [InlineData("duplicate-nested")]
    [InlineData("bad-receipt-header")]
    public async Task InvalidRequests_AdmitNothing(string scenario)
    {
        await using var factory = new PulseApiFactory(); using var client = factory.CreateClient(); var project = await SetupAsync(client);
        var id = scenario == "bad-id" ? "invalid" : scenario == "empty-id" ? Guid.Empty.ToString() : Guid.NewGuid().ToString();
        var extra = scenario == "duplicate-top-level" ? ",\"event\":\"again\"" : "";
        var properties = scenario == "duplicate-nested" ? "{\"items\":[{\"x\":1,\"x\":2}]}" : "{}";
        var raw = $$"""{"api_key":"{{project.ApiKey}}","event_id":"{{id}}","event":"a","distinct_id":"p","properties":{{properties}}{{extra}}} """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/capture") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        if (scenario == "bad-receipt-header") request.Headers.Add("X-Capture-Receipt", "sometimes");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, await db.QueuedEvents.CountAsync()); Assert.Equal(0, await db.CaptureProcessingItems.CountAsync()); Assert.Equal(0, await db.CaptureReceipts.CountAsync());
    }
}
