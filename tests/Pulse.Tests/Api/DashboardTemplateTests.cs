using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Api;

public class DashboardTemplateTests(PulseApiFactory factory) : IClassFixture<PulseApiFactory>
{
    private async Task<(HttpClient Client, ProjectResponse Project)> SetupAsync()
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Templates" });
        return (client, project);
    }
    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task RoundTrip_RemapsEveryId_PreservesSharing_AndQueriesDestination()
    {
        var (sourceClient, source) = await SetupAsync();
        var (destinationClient, destination) = await SetupAsync();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = from.AddDays(2);
        var sourceRoot = $"/api/projects/{source.Id}/dashboards";
        var destinationRoot = $"/api/projects/{destination.Id}/dashboards";
        var insight = await PostAsync<InsightResponse>(sourceClient, $"/api/projects/{source.Id}/insights",
            new { name = "Signups", type = "trend", config = new { @event = "signup", from, to } });
        var original = await PostAsync<DashboardResponse>(sourceClient, sourceRoot, new { name = "Growth", description = "Shared query" });
        for (var index = 0; index < 2; index++)
            await PostAsync<DashboardTileResponse>(sourceClient, sourceRoot + $"/{original.Id}/tiles", new { insightId = insight.Id, layout = new { x = index, color = "blue" } });
        var template = await sourceClient.GetFromJsonAsync<JsonElement>(sourceRoot + $"/{original.Id}/template");
        Assert.Equal(1, template.GetProperty("version").GetInt32());
        Assert.Equal(1, template.GetProperty("insights").GetArrayLength());
        Assert.DoesNotContain(source.Id.ToString(), template.GetRawText());
        Assert.DoesNotContain(insight.Id.ToString(), template.GetRawText());
        Assert.DoesNotContain(source.ApiKey, template.GetRawText());
        var imported = await PostAsync<DashboardResponse>(destinationClient, destinationRoot + "/import", template);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(destination.Id, imported.ProjectId);
        Assert.Equal("Growth", imported.Name);
        Assert.Equal("Shared query", imported.Description);
        Assert.Equal(2, imported.Tiles.Count);
        Assert.Single(imported.Tiles.Select(t => t.InsightId).Distinct());
        Assert.NotEqual(insight.Id, imported.Tiles[0].InsightId);
        Assert.All(imported.Tiles, t => Assert.Equal("blue", t.Layout.GetProperty("color").GetString()));
        var again = await PostAsync<DashboardResponse>(destinationClient, destinationRoot + "/import", template);
        Assert.NotEqual(imported.Id, again.Id);
        Assert.NotEqual(imported.Tiles[0].InsightId, again.Tiles[0].InsightId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Events.Add(new AnalyticsEvent { ProjectId = source.Id, Name = "signup", DistinctId = "source", Timestamp = from.AddHours(1) });
        db.Events.AddRange(Enumerable.Range(0, 3).Select(index => new AnalyticsEvent { ProjectId = destination.Id, Name = "signup", DistinctId = "dest-" + index, Timestamp = from.AddHours(1) }));
        await db.SaveChangesAsync();
        var refreshed = await PostAsync<JsonElement>(destinationClient, destinationRoot + $"/{imported.Id}/refresh", new { });
        Assert.All(refreshed.GetProperty("tiles").EnumerateArray(), tile => Assert.Equal(3,
            tile.GetProperty("result").GetProperty("buckets").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32())));
        Assert.Equal(HttpStatusCode.NotFound, (await destinationClient.GetAsync(sourceRoot + $"/{original.Id}/template")).StatusCode);
    }

    private static JsonObject Document() => JsonNode.Parse("""
        {"version":1,"name":"Portable","description":"Test","insights":[
          {"ref":"query-1","name":"Signups","type":"trend","config":{"event":"signup"}}],
         "tiles":[{"insightRef":"query-1","layout":{"x":0}}]}
        """)!.AsObject();

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("unused")]
    [InlineData("dangling")]
    [InlineData("cohort")]
    [InlineData("config")]
    [InlineData("layout")]
    [InlineData("insight-limit")]
    [InlineData("tile-limit")]
    public async Task InvalidGraph_CreatesNoDestinationRows(string kind)
    {
        var (client, project) = await SetupAsync();
        var document = Document();
        var insights = document["insights"]!.AsArray();
        var tiles = document["tiles"]!.AsArray();
        switch (kind)
        {
            case "version": document["version"] = 2; break;
            case "duplicate": insights.Add(insights[0]!.DeepClone()); break;
            case "unused": tiles.Clear(); break;
            case "dangling": tiles[0]!["insightRef"] = "QUERY-1"; break;
            case "cohort": insights[0]!["config"]!["filters"] = JsonSerializer.SerializeToNode(new[] { new { type = "cohort", value = Guid.NewGuid() } }); break;
            case "config": insights[0]!["config"]!["interval"] = "0"; break;
            case "layout": tiles[0]!["layout"] = new JsonArray(); break;
            case "insight-limit": while (insights.Count < 51) insights.Add(insights[0]!.DeepClone()); break;
            case "tile-limit": while (tiles.Count < 101) tiles.Add(tiles[0]!.DeepClone()); break;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{project.Id}/dashboards/import", document)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(0, await db.Dashboards.CountAsync(d => d.ProjectId == project.Id));
        Assert.Equal(0, await db.Insights.CountAsync(i => i.ProjectId == project.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyLimit_IsEnforced_WithOrWithoutContentLength(bool chunked)
    {
        var (client, project) = await SetupAsync();
        var bytes = Encoding.UTF8.GetBytes(Document().ToJsonString() + new string(' ', 256 * 1024));
        using HttpContent content = chunked ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync($"/api/projects/{project.Id}/dashboards/import", content)).StatusCode);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("invalid-config")]
    [InlineData("cohort")]
    [InlineData("foreign-reference")]
    public async Task UnsupportedExportSource_ReturnsConflictWithoutMutation(string kind)
    {
        var (client, project) = await SetupAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var dashboard = new Dashboard { ProjectId = project.Id, Name = "Source" };
        var insight = new Insight { ProjectId = kind == "foreign-reference" ? Guid.NewGuid() : project.Id, Name = "Source query",
            ConfigJson = kind switch
            {
                "invalid-json" => "not-json",
                "invalid-config" => "{}",
                "cohort" => JsonSerializer.Serialize(new { @event = "signup", filters = new[] { new { type = "cohort", value = Guid.NewGuid() } } }),
                _ => "{\"event\":\"signup\"}",
            } };
        db.Dashboards.Add(dashboard);
        db.Insights.Add(insight);
        db.DashboardTiles.Add(new DashboardTile { DashboardId = dashboard.Id, InsightId = insight.Id });
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/projects/{project.Id}/dashboards/{dashboard.Id}/template")).StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(insight.ConfigJson, (await db.Insights.SingleAsync(i => i.Id == insight.Id)).ConfigJson);
        Assert.Equal(1, await db.Dashboards.CountAsync(d => d.ProjectId == project.Id));
    }
}
