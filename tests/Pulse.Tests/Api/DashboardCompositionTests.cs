using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Api;

public sealed class DashboardCompositionFactory : PulseApiFactory
{
    public ConcurrentDictionary<Guid, int> Executions { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddScoped<IInsightRunner>(provider =>
            new CountingRunner(provider.GetRequiredService<InsightRunnerService>(), Executions)));
    }
    private sealed class CountingRunner(InsightRunnerService inner, ConcurrentDictionary<Guid, int> executions) : IInsightRunner
    {
        public Task<InsightRunResult> RunAsync(Insight insight, CancellationToken ct = default)
        {
            executions.AddOrUpdate(insight.Id, 1, (_, count) => count + 1);
            return inner.RunAsync(insight, ct);
        }
    }
}

public class DashboardCompositionTests(DashboardCompositionFactory factory) : IClassFixture<DashboardCompositionFactory>
{
    private record Scenario(HttpClient Client, ProjectResponse Project, DashboardResponse Dashboard, InsightResponse Insight, DashboardTileResponse[] Tiles)
    {
        public string Url => $"/api/projects/{Project.Id}/dashboards/{Dashboard.Id}";
    }

    private async Task<Scenario> CreateAsync(int count = 3)
    {
        var client = factory.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Composition" });
        var root = $"/api/projects/{project.Id}";
        var insight = await PostAsync<InsightResponse>(client, root + "/insights", new { name = "Shared", type = "trend", config = new { @event = "signup" } });
        var dashboard = await PostAsync<DashboardResponse>(client, root + "/dashboards", new { name = "Original", description = "Preserve description" });
        var tiles = new List<DashboardTileResponse>();
        for (var i = 0; i < count; i++)
            tiles.Add(await PostAsync<DashboardTileResponse>(client, root + $"/dashboards/{dashboard.Id}/tiles",
                new { insightId = insight.Id, layout = new { x = 0, y = i, w = 6, h = 2, color = "blue" } }));
        return new(client, project, dashboard, insight, tiles.ToArray());
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Copy_CreatesNewOwnedRows_AndSharesInsights()
    {
        var s = await CreateAsync(2);
        using var response = await s.Client.PostAsJsonAsync(s.Url + "/duplicate", new { name = "  Copy  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = (await response.Content.ReadFromJsonAsync<DashboardResponse>())!;
        Assert.EndsWith(copy.Id.ToString(), response.Headers.Location!.ToString());
        Assert.NotEqual(s.Dashboard.Id, copy.Id);
        Assert.Equal("Copy", copy.Name);
        Assert.Equal(s.Dashboard.Description, copy.Description);
        Assert.Equal(2, copy.Tiles.Count);
        Assert.All(copy.Tiles, t => { Assert.Equal(s.Insight.Id, t.InsightId); Assert.DoesNotContain(t.Id, s.Tiles.Select(old => old.Id)); });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        Assert.Equal(2, await db.Dashboards.CountAsync(d => d.ProjectId == s.Project.Id));
        Assert.Equal(1, await db.Insights.CountAsync(i => i.ProjectId == s.Project.Id));
        Assert.Equal(4, await db.DashboardTiles.CountAsync(t => t.DashboardId == copy.Id || t.DashboardId == s.Dashboard.Id));
        var moved = await s.Client.PutAsJsonAsync($"/api/projects/{s.Project.Id}/dashboards/{copy.Id}/tiles/{copy.Tiles[0].Id}", new { layout = new { x = 9 } });
        moved.EnsureSuccessStatusCode();
        var original = (await s.Client.GetFromJsonAsync<DashboardResponse>(s.Url))!;
        Assert.All(original.Tiles, t => Assert.Equal(0, t.Layout.GetProperty("x").GetInt32()));
    }

    [Fact]
    public async Task Copy_EmptyAndNameBoundaries_AndBrokenReferences()
    {
        var s = await CreateAsync(0);
        Assert.Empty((await PostAsync<DashboardResponse>(s.Client, s.Url + "/duplicate", new { name = "Empty" })).Tiles);
        foreach (var name in new[] { " ", new string('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(s.Url + "/duplicate", new { name })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var other = new Insight { ProjectId = Guid.NewGuid(), Name = "Foreign" };
        db.Insights.Add(other);
        db.DashboardTiles.Add(new DashboardTile { DashboardId = s.Dashboard.Id, InsightId = other.Id });
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(s.Url + "/duplicate", new { name = "Broken" })).StatusCode);
        Assert.Equal(2, await db.Dashboards.CountAsync(d => d.ProjectId == s.Project.Id));
    }

    [Fact]
    public async Task Copy_Rejects101Tiles_WithoutPartialRows()
    {
        var s = await CreateAsync(0);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.DashboardTiles.AddRange(Enumerable.Range(0, 101).Select(_ => new DashboardTile { DashboardId = s.Dashboard.Id, InsightId = s.Insight.Id }));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(s.Url + "/duplicate", new { name = "Too big" })).StatusCode);
        Assert.Equal(1, await db.Dashboards.CountAsync(d => d.ProjectId == s.Project.Id));
        Assert.Equal(101, await db.DashboardTiles.CountAsync(t => t.DashboardId == s.Dashboard.Id));
    }

    [Fact]
    public async Task LayoutBatch_UpdatesOnlySelectedTiles_PreservesExtraFields()
    {
        var s = await CreateAsync();
        using var response = await s.Client.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = s.Tiles.Take(2).Select(t =>
            new { tileId = t.Id, layout = new { x = 6, y = 10000, w = 6, h = 10000, color = "green" } }) });
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<DashboardResponse>())!;
        foreach (var tile in result.Tiles)
            Assert.Equal(tile.Id == s.Tiles[2].Id ? "blue" : "green", tile.Layout.GetProperty("color").GetString());
    }

    [Theory]
    [InlineData("{\"x\":7,\"y\":0,\"w\":6,\"h\":1}")]
    [InlineData("{\"x\":0.5,\"y\":0,\"w\":6,\"h\":1}")]
    [InlineData("{\"x\":0,\"y\":0,\"w\":6}")]
    [InlineData("{\"x\":0,\"y\":10001,\"w\":6,\"h\":1}")]
    [InlineData("{\"x\":0,\"y\":0,\"w\":6,\"h\":0}")]
    [InlineData("null")]
    public async Task LayoutBatch_RejectsMixedValidityWithoutAnyMutation(string raw)
    {
        var s = await CreateAsync();
        using var response = await s.Client.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = new object[]
        {
            new { tileId = s.Tiles[0].Id, layout = new { x = 6, y = 2, w = 6, h = 1 } },
            new { tileId = s.Tiles[1].Id, layout = JsonSerializer.Deserialize<JsonElement>(raw) },
        } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.All((await s.Client.GetFromJsonAsync<DashboardResponse>(s.Url))!.Tiles, t => Assert.Equal(0, t.Layout.GetProperty("x").GetInt32()));
    }

    [Fact]
    public async Task LayoutBatch_RejectsDuplicateEmptyOversizedAndCrossDashboardSelections()
    {
        var s = await CreateAsync();
        var entry = new { tileId = s.Tiles[0].Id, layout = new { x = 1, y = 1, w = 1, h = 1 } };
        foreach (var count in new[] { 0, 2, 101 })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = Enumerable.Repeat(entry, count) })).StatusCode);
        var otherDashboard = await PostAsync<DashboardResponse>(s.Client, $"/api/projects/{s.Project.Id}/dashboards", new { name = "Other" });
        var otherTile = await PostAsync<DashboardTileResponse>(s.Client, $"/api/projects/{s.Project.Id}/dashboards/{otherDashboard.Id}/tiles", new { insightId = s.Insight.Id });
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = new[] { entry, entry with { tileId = otherTile.Id } } })).StatusCode);
        var foreign = await CreateAsync(1);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = new[] { entry, entry with { tileId = foreign.Tiles[0].Id } } })).StatusCode);
        Assert.All((await s.Client.GetFromJsonAsync<DashboardResponse>(s.Url))!.Tiles, t => Assert.Equal(0, t.Layout.GetProperty("x").GetInt32()));
    }

    [Fact]
    public async Task SelectedRefresh_PreservesOrder_ReusesRealRunnerOnlyWithinRequest()
    {
        var s = await CreateAsync();
        var selected = new[] { s.Tiles[1].Id, s.Tiles[0].Id };
        var result = await PostAsync<JsonElement>(s.Client, s.Url + "/refresh-selection", new { tileIds = selected });
        Assert.Equal(selected, result.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("tileId").GetGuid()));
        Assert.All(result.GetProperty("tiles").EnumerateArray(), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("error").ValueKind));
        Assert.Equal(1, factory.Executions[s.Insight.Id]);
        await PostAsync<JsonElement>(s.Client, s.Url + "/refresh-selection", new { tileIds = selected });
        Assert.Equal(2, factory.Executions[s.Insight.Id]);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync(s.Url + "/refresh-selection", new { tileIds = new[] { s.Tiles[0].Id, Guid.NewGuid() } })).StatusCode);
        Assert.Equal(2, factory.Executions[s.Insight.Id]);
        foreach (var ids in new[] { Array.Empty<Guid>(), new[] { selected[0], selected[0] }, Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(s.Url + "/refresh-selection", new { tileIds = ids })).StatusCode);
        Assert.Equal(2, factory.Executions[s.Insight.Id]);
    }

    [Fact]
    public async Task SelectedRefresh_ReportsBrokenConfigAndReferences_AsPerTileErrors()
    {
        var s = await CreateAsync(1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var broken = new Insight { ProjectId = s.Project.Id, Name = "Broken", ConfigJson = "{}" };
        var foreign = new Insight { ProjectId = Guid.NewGuid(), Name = "Do not disclose", ConfigJson = "{}" };
        var badTile = new DashboardTile { DashboardId = s.Dashboard.Id, InsightId = broken.Id };
        var foreignTile = new DashboardTile { DashboardId = s.Dashboard.Id, InsightId = foreign.Id };
        db.Insights.AddRange(broken, foreign);
        db.DashboardTiles.AddRange(badTile, foreignTile);
        await db.SaveChangesAsync();
        var result = await PostAsync<JsonElement>(s.Client, s.Url + "/refresh-selection", new { tileIds = new[] { badTile.Id, s.Tiles[0].Id, foreignTile.Id } });
        var tiles = result.GetProperty("tiles").EnumerateArray().ToArray();
        Assert.NotEqual(JsonValueKind.Null, tiles[0].GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, tiles[1].GetProperty("error").ValueKind);
        Assert.Contains("unavailable", tiles[2].GetProperty("error").GetString());
        Assert.DoesNotContain("Do not disclose", result.GetRawText());
        Assert.False(factory.Executions.ContainsKey(foreign.Id));
    }

    [Fact]
    public async Task NewRoutes_HideProjectsFromOutsiders_AndRejectWriteKeys()
    {
        var s = await CreateAsync(1);
        var outsider = factory.CreateClient();
        await TestAuth.AuthenticateAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(s.Url + "/duplicate", new { name = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync(s.Url + "/refresh-selection", new { tileIds = new[] { s.Tiles[0].Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync(s.Url + "/tile-layouts", new { tiles = new[] { new { tileId = s.Tiles[0].Id, layout = new { x = 0, y = 0, w = 1, h = 1 } } } })).StatusCode);
        var keyClient = factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", s.Project.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsJsonAsync(s.Url + "/duplicate", new { name = "No" })).StatusCode);
    }

    private sealed class CancellableRunner : IInsightRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<InsightRunResult> RunAsync(Insight insight, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("The cancellation probe must never return a successful tile.");
        }
    }

    [Fact]
    public async Task SelectedRefresh_PropagatesCancellationToRunner()
    {
        var runner = new CancellableRunner();
        await using var owner = new PulseApiFactory();
        await using var server = owner.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IInsightRunner>(runner)));
        using var client = server.CreateClient();
        await TestAuth.AuthenticateAsync(client);
        var project = await PostAsync<ProjectResponse>(client, "/api/projects", new { name = "Cancellation" });
        var root = $"/api/projects/{project.Id}";
        var insight = await PostAsync<InsightResponse>(client, root + "/insights", new { name = "Query", type = "trend", config = new { @event = "signup" } });
        var dashboard = await PostAsync<DashboardResponse>(client, root + "/dashboards", new { name = "Board" });
        var tile = await PostAsync<DashboardTileResponse>(client, root + $"/dashboards/{dashboard.Id}/tiles", new { insightId = insight.Id });
        using var cancellation = new CancellationTokenSource();
        var request = client.PostAsJsonAsync(root + $"/dashboards/{dashboard.Id}/refresh-selection", new { tileIds = new[] { tile.Id } }, cancellation.Token);
        try
        {
            await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await runner.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            // A failed assertion/entry timeout must still release the infinite
            // probe before the host is disposed. CancellationTokenSource.Dispose
            // does not cancel its token.
            cancellation.Cancel();
        }
    }
}
