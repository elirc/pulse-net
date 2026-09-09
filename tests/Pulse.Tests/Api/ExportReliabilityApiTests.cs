using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pulse.Api.Contracts;
using Pulse.Api.Export;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public class ExportReliabilityApiTests
{
    [Fact]
    public async Task SnapshotAdmission_CancelAndHistory_UseNewStatesWithoutPublishing()
    {
        await using var parent = new PulseApiFactory();
        await using var server = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ExportWorker)))));
        using var client = server.CreateClient(); await TestAuth.AuthenticateAsync(client);
        var project = (await (await client.PostAsJsonAsync("/api/projects", new { name = "Snapshot API" })).Content.ReadFromJsonAsync<ProjectResponse>())!;
        var root = $"/api/projects/{project.Id}/exports";
        var from = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        foreach (var body in new object[] {
            new { type = "persons", consistency = "snapshot", from, to = from },
            new { type = "events", consistency = "unknown", from, to = from },
            new { type = "events", consistency = "snapshot" },
            new { type = "events", consistency = "snapshot", from, to = from.AddDays(91) },
            new { type = "events", consistency = "snapshot", from, to = from.AddSeconds(-1) },
            new { type = "events", consistency = "snapshot", from, to = from, filters = new[] { new { key = "x", @operator = "exact", value = "y" } } }
        }) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root, body)).StatusCode);
        var create = await client.PostAsJsonAsync(root, new { type = "events", consistency = "snapshot", from, to = from.AddDays(90), filters = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode); var job = (await create.Content.ReadFromJsonAsync<ExportJobResponse>())!;
        Assert.Equal("snapshot", job.Consistency); Assert.Null(job.SnapshotCapturedAt);
        var path = root + "/" + job.Id;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "/cancel", null)).StatusCode);
        Assert.Equal("cancelled", (await client.GetFromJsonAsync<ExportJobResponse>(path))!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path + "/download")).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>(root + "?status=cancelled"); Assert.Single(history.GetProperty("jobs").EnumerateArray());
        using var scope = server.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var running = new ExportJob { ProjectId = project.Id, Type = "events", Format = "json" }; db.ExportJobs.Add(running); await db.SaveChangesAsync();
        var owner = scope.ServiceProvider.GetRequiredService<ExportOwnershipService>();
        Assert.NotNull(await owner.ClaimAsync(project.Id, running.Id, Guid.NewGuid(), default));
        var cancelled = await client.PostAsync(root + "/" + running.Id + "/cancel", null); Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal("cancelRequested", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        history = await client.GetFromJsonAsync<JsonElement>(root + "?status=cancelRequested"); Assert.Single(history.GetProperty("jobs").EnumerateArray());
        using var outsider = server.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.PostAsync(path + "/cancel", null)).StatusCode);
        await TestAuth.AuthenticateAsync(outsider); Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync(path + "/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
    }
}
