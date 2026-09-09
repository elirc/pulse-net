using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class DashboardEndpoints
{
    private static void MapDashboardCompositionFeatures(RouteGroupBuilder group)
    {
        group.MapPost("/{dashboardId:guid}/duplicate", async (Guid projectId, Guid dashboardId,
            DuplicateDashboardRequest request, HttpContext http, ProjectAccessService access,
            DashboardCopyService copies, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(request.Name, 200, "name", out var name, required: true) is { } invalid) return invalid;
            var result = await copies.CopyAsync(projectId, dashboardId, name!, ct);
            if (result.Missing) return Results.NotFound();
            if (result.Error is { } error) return Results.Problem(error, statusCode: 409);
            var copy = result.Dashboard!;
            return Results.Created($"/api/projects/{projectId}/dashboards/{copy.Id}",
                ToResponse(copy, await LoadTilesAsync(db, copy.Id, ct)));
        });

        group.MapPut("/{dashboardId:guid}/tile-layouts", async (Guid projectId, Guid dashboardId,
            BatchTileLayoutsRequest request, HttpContext http, ProjectAccessService access,
            PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.Tiles is not { Count: >= 1 and <= 100 } inputs)
                return Invalid("tiles", "Supply between 1 and 100 tiles.");
            var layouts = new Dictionary<Guid, string>();
            for (var index = 0; index < inputs.Count; index++)
            {
                var input = inputs[index];
                if (input is null) return Invalid($"tiles[{index}]", "A tile object is required.");
                if (layouts.ContainsKey(input.TileId)) return Invalid($"tiles[{index}].tileId", "Tile IDs must be unique.");
                if (ValidateGrid(input.Layout, $"tiles[{index}].layout") is { } error) return error;
                layouts.Add(input.TileId, input.Layout!.Value.GetRawText());
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var dashboard = await db.Dashboards.SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);
            if (dashboard is null) return Results.NotFound();
            var ids = layouts.Keys.ToArray();
            var tiles = await db.DashboardTiles.Where(t => t.DashboardId == dashboardId && ids.Contains(t.Id)).ToListAsync(ct);
            if (tiles.Count != ids.Length) return Results.NotFound();
            foreach (var tile in tiles) tile.LayoutJson = layouts[tile.Id];
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(ToResponse(dashboard, await LoadTilesAsync(db, dashboardId, ct)));
        });

        group.MapPost("/{dashboardId:guid}/refresh-selection", async (Guid projectId, Guid dashboardId,
            SelectedTileRefreshRequest request, HttpContext http, ProjectAccessService access,
            PulseDbContext db, IInsightRunner runner, TimeProvider clock, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.TileIds is not { Count: >= 1 and <= 50 } ids || ids.Distinct().Count() != ids.Count)
                return Invalid("tileIds", "Supply between 1 and 50 unique tile IDs.");
            var dashboard = await db.Dashboards.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);
            if (dashboard is null) return Results.NotFound();
            var tiles = await db.DashboardTiles.AsNoTracking().Where(t => t.DashboardId == dashboardId && ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);
            if (tiles.Count != ids.Count) return Results.NotFound();
            var insightIds = tiles.Values.Select(t => t.InsightId).Distinct().ToArray();
            var insights = await db.Insights.AsNoTracking().Where(i => i.ProjectId == projectId && insightIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, ct);
            var runs = new Dictionary<Guid, InsightRunResult>();
            var results = new List<TileRefreshResponse>();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var tile = tiles[id];
                insights.TryGetValue(tile.InsightId, out var insight);
                if (!runs.TryGetValue(tile.InsightId, out var run))
                {
                    run = insight is null ? InsightRunResult.Failure("Tile insight is unavailable.") : await runner.RunAsync(insight, ct);
                    runs.Add(tile.InsightId, run);
                }
                // Legacy layouts are normally objects; a corrupt stored layout is a tile error.
                JsonElement layout;
                try { layout = JsonSerializer.Deserialize<JsonElement>(tile.LayoutJson); }
                catch (JsonException) { layout = JsonSerializer.SerializeToElement(new { }); run = InsightRunResult.Failure("Tile layout is not valid JSON."); }
                results.Add(new TileRefreshResponse(tile.Id, tile.InsightId, insight?.Name ?? "Unavailable insight",
                    insight?.Type.ToString().ToLowerInvariant() ?? "unknown", layout, run.Result, run.Error));
            }
            return Results.Ok(new DashboardRefreshResponse(dashboard.Id, dashboard.Name, clock.GetUtcNow(), results));
        });
    }

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult? ValidateGrid(JsonElement? input, string path)
    {
        if (input is not { ValueKind: JsonValueKind.Object } layout) return Invalid(path, "Layout must be an object.");
        var values = new Dictionary<string, int>();
        foreach (var key in new[] { "x", "y", "w", "h" })
        {
            if (!layout.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                return Invalid(path + "." + key, "An integer is required.");
            values.Add(key, number);
        }
        if (values["x"] is < 0 or >= 12) return Invalid(path + ".x", "x must be between 0 and 11.");
        if (values["y"] is < 0 or > 10000) return Invalid(path + ".y", "y must be between 0 and 10000.");
        if (values["w"] is < 1 or > 12 || values["x"] + values["w"] > 12) return Invalid(path + ".w", "Width must be 1–12 and fit within the 12-column grid.");
        if (values["h"] is < 1 or > 10000) return Invalid(path + ".h", "h must be between 1 and 10000.");
        return null;
    }
}
