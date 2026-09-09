using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

public class ExportLifecycleTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private static async Task<ProjectResponse> ProjectAsync(HttpClient client)
    {
        await TestAuth.AuthenticateAsync(client);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Export lifecycle" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectResponse>())!;
    }

    [Theory]
    [InlineData("a,b\r\n1,2\r\n", "text/csv")]
    [InlineData("{\"message\":\"café 日本語\"}", "application/json")]
    [InlineData("", "text/csv")]
    public async Task Integrity_MatchesExactDownloadedUtf8Bytes(string content, string contentType)
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var job = new ExportJob { ProjectId = project.Id, Type = "events", Format = contentType == "text/csv" ? "csv" : "json",
            Status = ExportJobStatus.Completed, ResultContent = content, ContentType = contentType };
        db.ExportJobs.Add(job);
        await db.SaveChangesAsync();
        var path = $"/api/projects/{project.Id}/exports/{job.Id}";
        var integrity = (await client.GetFromJsonAsync<ExportIntegrity>(path + "/integrity"))!;
        var download = await client.GetAsync(path + "/download");
        download.EnsureSuccessStatusCode();
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(content), bytes);
        Assert.Equal(bytes.LongLength, integrity.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), integrity.Sha256);
        Assert.Equal("utf-8", integrity.Encoding);
        Assert.Equal(download.Content.Headers.ContentType!.ToString(), integrity.ContentType);
    }

    [Fact]
    public async Task TerminalDeletion_RejectsPendingAndRunning_AndRemovesDownloadWithoutTouchingEvents()
    {
        // A derived host shares its parent's database. Use a fresh parent so
        // the class fixture's running worker cannot consume these state fixtures.
        await using var isolated = new PulseApiFactory();
        await using var server = isolated.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(ExportWorker)))));
        using var client = server.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.Add(new AnalyticsEvent { ProjectId = project.Id, Name = "keep", DistinctId = "p" });
        var jobs = Enum.GetValues<ExportJobStatus>().Select(status => new ExportJob { ProjectId = project.Id, Type = "events", Format = "json",
            Status = status, ResultContent = status == ExportJobStatus.Completed ? "[]" : null }).ToArray();
        db.ExportJobs.AddRange(jobs);
        await db.SaveChangesAsync();
        foreach (var job in jobs)
        {
            var path = $"/api/projects/{project.Id}/exports/{job.Id}";
            var deletion = await client.DeleteAsync(path);
            if (job.Status is ExportJobStatus.Pending or ExportJobStatus.Running or ExportJobStatus.CancelRequested)
            {
                Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
                Assert.True(await db.ExportJobs.AnyAsync(j => j.Id == job.Id));
                Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path + "/integrity")).StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/download")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/integrity")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
            }
        }
        Assert.Equal(1, await db.Events.CountAsync(e => e.ProjectId == project.Id));
        var pending = jobs.Single(j => j.Status == ExportJobStatus.Pending);
        await db.ExportJobs.Where(j => j.Id == pending.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ExportJobStatus.Running));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/projects/{project.Id}/exports/{pending.Id}")).StatusCode);
        var missingContent = new ExportJob { ProjectId = project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Completed };
        db.ExportJobs.Add(missingContent);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/projects/{project.Id}/exports/{missingContent.Id}/integrity")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/projects/{project.Id}/exports/{missingContent.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task History_PagesTiesWithFilters_AndKeepsDocumentsOutOfResponses()
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var time = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var ids = Enumerable.Range(1, 3).Select(i => Guid.Parse($"00000000-0000-0000-0001-{i:000000000000}")).ToArray();
        db.ExportJobs.AddRange(ids.Select(id => new ExportJob { Id = id, ProjectId = project.Id, Type = "events", Format = "json",
            Status = ExportJobStatus.Completed, CreatedAt = time, ResultContent = "private-document-marker", ParamsJson = "private-parameters-marker" }));
        db.ExportJobs.Add(new ExportJob { ProjectId = project.Id, Type = "persons", Format = "json", Status = ExportJobStatus.Completed, CreatedAt = time });
        db.ExportJobs.Add(new ExportJob { ProjectId = Guid.NewGuid(), Type = "events", Format = "json", Status = ExportJobStatus.Completed, CreatedAt = time });
        await db.SaveChangesAsync();
        var root = $"/api/projects/{project.Id}/exports?status=completed&type=events&limit=1";
        string? cursor = null, firstCursor = null;
        var seen = new List<Guid>();
        do
        {
            var raw = await client.GetStringAsync(root + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.DoesNotContain("private-document-marker", raw);
            Assert.DoesNotContain("private-parameters-marker", raw);
            var page = JsonSerializer.Deserialize<JsonElement>(raw);
            seen.Add(page.GetProperty("jobs")[0].GetProperty("id").GetGuid());
            cursor = page.GetProperty("nextCursor").GetString();
            firstCursor ??= cursor;
        } while (cursor is not null);
        Assert.Equal(ids.Reverse(), seen);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project.Id}/exports?type=persons&cursor={firstCursor}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project.Id}/exports?status=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/projects/{project.Id}/exports?limit=101")).StatusCode);
        using var outsider = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await outsider.GetAsync(root)).StatusCode);
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(root)).StatusCode);
    }

    [Fact]
    public async Task Retry_CreatesIndependentJob_PreservesFailure_AndCanFailAgainForMissingInsight()
    {
        using var client = factory.CreateClient();
        var project = await ProjectAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        var sources = new[]
        {
            new ExportJob { ProjectId = project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Failed, Error = "original failure", CompletedAt = before },
            new ExportJob { ProjectId = project.Id, Type = "insight", Format = "json", ParamsJson = JsonSerializer.Serialize(new { insightId = Guid.NewGuid() }), Status = ExportJobStatus.Failed, Error = "missing insight", CompletedAt = before },
        };
        db.ExportJobs.AddRange(sources);
        await db.SaveChangesAsync();
        foreach (var source in sources)
        {
            var root = $"/api/projects/{project.Id}/exports/{source.Id}";
            var response = await client.PostAsync(root + "/retry", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(source.Id, result.GetProperty("sourceJobId").GetGuid());
            var id = result.GetProperty("job").GetProperty("id").GetGuid();
            Assert.NotEqual(source.Id, id);
            Assert.Contains(id.ToString(), response.Headers.Location!.ToString());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            ExportJobResponse? completed;
            do
            {
                completed = await client.GetFromJsonAsync<ExportJobResponse>($"/api/projects/{project.Id}/exports/{id}", deadline.Token);
                if (completed!.Status is "completed" or "failed") break;
                await Task.Delay(50, deadline.Token);
            } while (true);
            Assert.Equal(source.Type == "events" ? "completed" : "failed", completed.Status);
            var original = (await client.GetFromJsonAsync<ExportJobResponse>(root))!;
            Assert.Equal("failed", original.Status);
            Assert.Equal(source.Error, original.Error);
            Assert.Equal(before, original.CompletedAt);
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(root + "/download")).StatusCode);
            var copied = await db.ExportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
            Assert.Equal(source.ParamsJson, copied.ParamsJson);
        }
        var again = await client.PostAsync($"/api/projects/{project.Id}/exports/{sources[0].Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(5, await db.ExportJobs.CountAsync(j => j.ProjectId == project.Id));
    }
}
