using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class IngestionOperationsTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    [Fact]
    public async Task Replay_QueuesStoredPayload_ConsumesLetter_AndCannotBeRepeated()
    {
        using var client = factory.CreateClient();
        var project = await CreateProjectAsync(client);
        var letter = await SeedLetterAsync(project.Id);

        var response = await client.PostAsync(ReplayUrl(project.Id, letter.Id), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new CaptureResponse("queued", 1), await response.Content.ReadFromJsonAsync<CaptureResponse>());
        await TestIngestion.WaitForDrainAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(ReplayUrl(project.Id, letter.Id), null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var stored = Assert.Single(await db.Events.Where(e => e.ProjectId == project.Id).ToListAsync());
        Assert.Equal("recovered", stored.Name);
        Assert.Equal("{\"plan\":\"pro\"}", stored.PropertiesJson);
        Assert.False(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"Name\":\"event\",\"DistinctId\":\"u1\"}")]
    [InlineData("{\"Name\":\"event\",\"DistinctId\":\"u1\",\"PropertiesJson\":null}")]
    [InlineData("{\"Name\":\"event\",\"DistinctId\":\"u1\",\"PropertiesJson\":\" \"}")]
    [InlineData("{\"Name\":\"\",\"DistinctId\":\"u1\",\"PropertiesJson\":\"{}\"}")]
    [InlineData("{\"Name\":\"event\",\"DistinctId\":\"u1\",\"PropertiesJson\":\"{broken\"}")]
    [InlineData("{\"Name\":\"event\",\"DistinctId\":\"u1\",\"PropertiesJson\":\"[]\"}")]
    public async Task Replay_InvalidPayload_Returns422AndRetainsEvidence(string payload)
    {
        using var client = factory.CreateClient();
        var project = await CreateProjectAsync(client);
        var letter = await SeedLetterAsync(project.Id, payload);
        var response = await client.PostAsync(ReplayUrl(project.Id, letter.Id), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id));
        Assert.False(await db.QueuedEvents.AnyAsync(q => q.ProjectId == project.Id));
    }

    [Fact]
    public async Task Operations_RequireMembership_AndRejectProjectKeys()
    {
        using var owner = factory.CreateClient();
        var project = await CreateProjectAsync(owner);
        var letter = await SeedLetterAsync(project.Id);
        using var stranger = factory.CreateClient();
        using var anonymous = factory.CreateClient();
        await TestAuth.AuthenticateAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(MetricsUrl(project.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync(ReplayUrl(project.Id, letter.Id), null)).StatusCode);
        foreach (var key in new[] { "", project.ApiKey, project.ReadKey })
        {
            anonymous.DefaultRequestHeaders.Remove("X-Api-Key");
            if (key.Length > 0) anonymous.DefaultRequestHeaders.Add("X-Api-Key", key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(MetricsUrl(project.Id))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(ReplayUrl(project.Id, letter.Id), null)).StatusCode);
        }
    }

    [Fact]
    public async Task Operations_ScopeLettersAndMetricsToRequestedProject()
    {
        using var client = factory.CreateClient();
        var first = await CreateProjectAsync(client);
        var second = await CreateProjectAsync(client);
        var letter = await SeedLetterAsync(second.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(ReplayUrl(first.Id, letter.Id), null)).StatusCode);
        var firstMetrics = await client.GetFromJsonAsync<ProjectIngestionMetrics>(MetricsUrl(first.Id));
        var secondMetrics = await client.GetFromJsonAsync<ProjectIngestionMetrics>(MetricsUrl(second.Id));
        Assert.Equal(0, firstMetrics!.DeadLetters);
        Assert.Equal(1, secondMetrics!.DeadLetters);
        Assert.Null(firstMetrics.OldestPendingAgeSeconds);
    }

    [Fact]
    public async Task ConcurrentReplay_OnlyOneRequestQueuesTheLetter()
    {
        using var client = factory.CreateClient();
        var project = await CreateProjectAsync(client);
        var letter = await SeedLetterAsync(project.Id);
        var results = await Task.WhenAll(
            client.PostAsync(ReplayUrl(project.Id, letter.Id), null),
            client.PostAsync(ReplayUrl(project.Id, letter.Id), null));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NotFound);
        await TestIngestion.WaitForDrainAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Single(await db.Events.Where(e => e.ProjectId == project.Id).ToListAsync());
    }

    private static string MetricsUrl(Guid projectId) => $"/api/projects/{projectId}/ingestion/metrics";
    private static string ReplayUrl(Guid projectId, Guid letterId) => $"/api/projects/{projectId}/ingestion/dead-letters/{letterId}/replay";

    private static async Task<ProjectResponse> CreateProjectAsync(HttpClient client)
    {
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Learning operations" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }

    private async Task<DeadLetterEvent> SeedLetterAsync(Guid projectId, string? payload = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var letter = new DeadLetterEvent
        {
            ProjectId = projectId, Error = "Temporary dependency failure", Attempts = 2,
            PayloadJson = payload ?? JsonSerializer.Serialize(new IncomingEvent("recovered", "u1", null, "{\"plan\":\"pro\"}")),
        };
        db.DeadLetterEvents.Add(letter);
        await db.SaveChangesAsync();
        return letter;
    }
}
