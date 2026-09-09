using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class InsightEditingTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Insight editor" });
        return (client, project);
    }
    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Theory]
    [InlineData("trend", "{\"event\":\"signup\",\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-03T00:00:00Z\"}")]
    [InlineData("funnel", "{\"steps\":[\"signup\",\"activate\"],\"from\":\"2026-03-01T00:00:00Z\",\"to\":\"2026-03-03T00:00:00Z\"}")]
    [InlineData("retention", "{\"from\":\"2026-03-01\",\"days\":3,\"targetEvent\":\"signup\"}")]
    public async Task Preview_MatchesSavedQuery_WithoutPersistingTemporaryInsight(string type, string raw)
    {
        var (client, project) = await SetupAsync();
        var root = $"/api/projects/{project.Id}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var person = new Person { ProjectId = project.Id };
        db.Persons.Add(person);
        var time = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
        db.Events.AddRange(Enumerable.Range(0, 3).Select(index => new AnalyticsEvent
        {
            ProjectId = project.Id, PersonId = person.Id, DistinctId = "learner", Name = index == 2 ? "activate" : "signup", Timestamp = time.AddHours(index),
        }));
        await db.SaveChangesAsync();
        var config = JsonSerializer.Deserialize<JsonElement>(raw);
        var preview = await PostAsync<JsonElement>(client, root + "/insights/preview", new { type, config });
        Assert.Equal(type, preview.GetProperty("type").GetString());
        Assert.Equal(0, await db.Insights.CountAsync(i => i.ProjectId == project.Id));
        Assert.Equal(3, await db.Events.CountAsync(e => e.ProjectId == project.Id));
        var saved = await PostAsync<InsightResponse>(client, root + "/insights", new { name = "Equivalent", type, config });
        var exported = await client.GetFromJsonAsync<JsonElement>(root + $"/export/insights/{saved.Id}");
        Assert.True(JsonElement.DeepEquals(preview.GetProperty("result"), exported));
    }

    [Fact]
    public async Task Replace_ReportsAllErrors_PreservesState_AndKeepsRelativeOmissions()
    {
        var (client, project) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/insights";
        var saved = await PostAsync<InsightResponse>(client, root, new { name = "Old", type = "trend", config = new { @event = "old", breakdown = "plan" } });
        using var bad = await client.PutAsJsonAsync(root + $"/{saved.Id}", new { name = " ", type = "trend", config = new { @event = "new", interval = 2 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problem = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("config.interval", out _));
        var unchanged = (await client.GetFromJsonAsync<InsightResponse>(root + $"/{saved.Id}"))!;
        Assert.Equal("Old", unchanged.Name);
        Assert.True(unchanged.Config.TryGetProperty("breakdown", out _));
        using var changed = await client.PutAsJsonAsync(root + $"/{saved.Id}", new { name = " New ", type = "funnel", config = new { steps = new[] { " signup ", "activate" } } });
        changed.EnsureSuccessStatusCode();
        var updated = (await changed.Content.ReadFromJsonAsync<InsightResponse>())!;
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(saved.ProjectId, updated.ProjectId);
        Assert.Equal(saved.CreatedAt, updated.CreatedAt);
        Assert.Equal("New", updated.Name);
        Assert.Equal("funnel", updated.Type);
        Assert.False(updated.Config.TryGetProperty("breakdown", out _));
        Assert.False(updated.Config.TryGetProperty("from", out _));
        Assert.False(updated.Config.TryGetProperty("to", out _));
        Assert.Equal("signup", updated.Config.GetProperty("steps")[0].GetString());
    }

    [Fact]
    public async Task Usages_CountRepeatedTiles_BlockDelete_ThenAllowUnusedDelete()
    {
        var (client, project) = await SetupAsync();
        var root = $"/api/projects/{project.Id}";
        var insight = await PostAsync<InsightResponse>(client, root + "/insights", new { name = "Shared", type = "trend", config = new { @event = "signup" } });
        var b = await PostAsync<DashboardResponse>(client, root + "/dashboards", new { name = "B" });
        var a = await PostAsync<DashboardResponse>(client, root + "/dashboards", new { name = "A" });
        foreach (var dashboardId in new[] { a.Id, b.Id, a.Id })
            await PostAsync<DashboardTileResponse>(client, root + $"/dashboards/{dashboardId}/tiles", new { insightId = insight.Id });
        var usage = await client.GetFromJsonAsync<JsonElement>(root + $"/insights/{insight.Id}/usages");
        Assert.Equal(2, usage.GetProperty("dashboardCount").GetInt32());
        Assert.Equal(3, usage.GetProperty("tileCount").GetInt32());
        Assert.Equal(new[] { "A", "B" }, usage.GetProperty("dashboards").EnumerateArray().Select(d => d.GetProperty("name").GetString()));
        Assert.False(usage.GetProperty("truncated").GetBoolean());
        using var blocked = await client.DeleteAsync(root + $"/insights/{insight.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(3, (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tileCount").GetInt32());
        (await client.DeleteAsync(root + $"/dashboards/{a.Id}")).EnsureSuccessStatusCode();
        (await client.DeleteAsync(root + $"/dashboards/{b.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(root + $"/insights/{insight.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + $"/insights/{insight.Id}/usages")).StatusCode);
    }

    [Fact]
    public async Task InvalidPreviewAndOutsiderRequests_CreateNoInsights()
    {
        var (client, project) = await SetupAsync();
        var root = $"/api/projects/{project.Id}/insights";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/preview", new { type = "0", config = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(root + "/preview", new { type = "trend", config = new { @event = "signup", description = new string('x', 33000) } })).StatusCode);
        var outsider = factory.CreateClient();
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(root + "/preview", new { type = "trend", config = new { @event = "signup" } })).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<PulseDbContext>().Insights.CountAsync(i => i.ProjectId == project.Id));
    }

    [Fact]
    public async Task Replacement_ChangesBothDashboardQueries_ButKeepsCompletedDownload()
    {
        var (client, project) = await SetupAsync();
        var root = $"/api/projects/{project.Id}";
        const string from = "2026-03-01T00:00:00Z", to = "2026-03-02T00:00:00Z";
        var insight = await PostAsync<InsightResponse>(client, root + "/insights", new { name = "Shared", type = "trend", config = new { @event = "old", from, to } });
        var dashboards = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var dashboard = await PostAsync<DashboardResponse>(client, root + "/dashboards", new { name = "Board " + index });
            dashboards.Add(dashboard.Id);
            await PostAsync<DashboardTileResponse>(client, root + $"/dashboards/{dashboard.Id}/tiles", new { insightId = insight.Id });
        }
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var timestamp = DateTimeOffset.Parse(from).AddHours(1);
        db.Events.AddRange(new AnalyticsEvent { ProjectId = project.Id, Name = "old", DistinctId = "p", Timestamp = timestamp },
            new AnalyticsEvent { ProjectId = project.Id, Name = "new", DistinctId = "p", Timestamp = timestamp },
            new AnalyticsEvent { ProjectId = project.Id, Name = "new", DistinctId = "q", Timestamp = timestamp });
        const string frozen = "{\"previousCount\":1}";
        var export = new ExportJob { ProjectId = project.Id, Type = "insight", Format = "json", Status = ExportJobStatus.Completed,
            ResultContent = frozen, ContentType = "application/json", RowCount = 1, CompletedAt = timestamp };
        db.ExportJobs.Add(export);
        await db.SaveChangesAsync();
        foreach (var dashboardId in dashboards)
        {
            var result = await PostAsync<JsonElement>(client, root + $"/dashboards/{dashboardId}/refresh", new { });
            Assert.Equal(1, result.GetProperty("tiles")[0].GetProperty("result").GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        }
        (await client.PutAsJsonAsync(root + $"/insights/{insight.Id}", new { name = "Updated", type = "trend", config = new { @event = "new", from, to } })).EnsureSuccessStatusCode();
        foreach (var dashboardId in dashboards)
        {
            var result = await PostAsync<JsonElement>(client, root + $"/dashboards/{dashboardId}/refresh", new { });
            Assert.Equal(2, result.GetProperty("tiles")[0].GetProperty("result").GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        }
        Assert.Equal(frozen, await client.GetStringAsync(root + $"/exports/{export.Id}/download"));
    }
}
