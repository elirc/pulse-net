using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/dashboards");

        group.MapPost("/", async (
            Guid projectId,
            CreateDashboardRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Dashboard name is required."],
                });
            }

            var dashboard = new Dashboard
            {
                ProjectId = projectId,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
            };

            db.Dashboards.Add(dashboard);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/dashboards/{dashboard.Id}",
                ToResponse(dashboard, []));
        });

        group.MapGet("/", async (
            Guid projectId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboards = await (
                from d in db.Dashboards
                where d.ProjectId == projectId
                orderby d.CreatedAt
                select new DashboardSummaryResponse(
                    d.Id,
                    d.ProjectId,
                    d.Name,
                    d.Description,
                    db.DashboardTiles.Count(t => t.DashboardId == d.Id),
                    d.CreatedAt)).ToListAsync(ct);

            return Results.Ok(dashboards);
        });

        group.MapGet("/{dashboardId:guid}", async (
            Guid projectId,
            Guid dashboardId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboard = await db.Dashboards
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

            if (dashboard is null)
            {
                return Results.NotFound();
            }

            var tiles = await LoadTilesAsync(db, dashboardId, ct);
            return Results.Ok(ToResponse(dashboard, tiles));
        });

        group.MapPut("/{dashboardId:guid}", async (
            Guid projectId,
            Guid dashboardId,
            UpdateDashboardRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboard = await db.Dashboards
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

            if (dashboard is null)
            {
                return Results.NotFound();
            }

            if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Dashboard name must not be blank."],
                });
            }

            dashboard.Name = request.Name?.Trim() ?? dashboard.Name;
            dashboard.Description = request.Description?.Trim() ?? dashboard.Description;
            await db.SaveChangesAsync(ct);

            var tiles = await LoadTilesAsync(db, dashboardId, ct);
            return Results.Ok(ToResponse(dashboard, tiles));
        });

        group.MapDelete("/{dashboardId:guid}", async (
            Guid projectId,
            Guid dashboardId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboard = await db.Dashboards
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

            if (dashboard is null)
            {
                return Results.NotFound();
            }

            await db.DashboardTiles.Where(t => t.DashboardId == dashboardId).ExecuteDeleteAsync(ct);
            db.Dashboards.Remove(dashboard);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        // --- Tiles ---------------------------------------------------------

        group.MapPost("/{dashboardId:guid}/tiles", async (
            Guid projectId,
            Guid dashboardId,
            AddTileRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboard = await db.Dashboards
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

            if (dashboard is null)
            {
                return Results.NotFound();
            }

            if (request.InsightId is not { } insightId)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["insightId"] = ["insightId is required."],
                });
            }

            var insight = await db.Insights
                .SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);

            if (insight is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["insightId"] = ["No such insight in this project."],
                });
            }

            var tile = new DashboardTile
            {
                DashboardId = dashboardId,
                InsightId = insightId,
                LayoutJson = request.Layout is { ValueKind: JsonValueKind.Object } layout
                    ? layout.GetRawText()
                    : "{}",
            };

            db.DashboardTiles.Add(tile);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/dashboards/{dashboardId}/tiles/{tile.Id}",
                ToTileResponse(tile, insight));
        });

        group.MapPut("/{dashboardId:guid}/tiles/{tileId:guid}", async (
            Guid projectId,
            Guid dashboardId,
            Guid tileId,
            UpdateTileRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var tile = await FindTileAsync(db, projectId, dashboardId, tileId, ct);
            if (tile is null)
            {
                return Results.NotFound();
            }

            if (request.Layout is { ValueKind: JsonValueKind.Object } layout)
            {
                tile.LayoutJson = layout.GetRawText();
                await db.SaveChangesAsync(ct);
            }

            var insight = await db.Insights.SingleAsync(i => i.Id == tile.InsightId, ct);
            return Results.Ok(ToTileResponse(tile, insight));
        });

        group.MapDelete("/{dashboardId:guid}/tiles/{tileId:guid}", async (
            Guid projectId,
            Guid dashboardId,
            Guid tileId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var tile = await FindTileAsync(db, projectId, dashboardId, tileId, ct);
            if (tile is null)
            {
                return Results.NotFound();
            }

            db.DashboardTiles.Remove(tile);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        // --- Refresh: run every tile's query in one response -----------------

        group.MapPost("/{dashboardId:guid}/refresh", async (
            Guid projectId,
            Guid dashboardId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            InsightRunnerService runner,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var dashboard = await db.Dashboards
                .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

            if (dashboard is null)
            {
                return Results.NotFound();
            }

            var tiles = await (
                from t in db.DashboardTiles
                join i in db.Insights on t.InsightId equals i.Id
                where t.DashboardId == dashboardId
                orderby t.CreatedAt
                select new { Tile = t, Insight = i }).ToListAsync(ct);

            var results = new List<TileRefreshResponse>(tiles.Count);
            foreach (var entry in tiles)
            {
                var run = await runner.RunAsync(entry.Insight, ct);
                results.Add(new TileRefreshResponse(
                    entry.Tile.Id,
                    entry.Insight.Id,
                    entry.Insight.Name,
                    entry.Insight.Type.ToString().ToLowerInvariant(),
                    JsonSerializer.Deserialize<JsonElement>(entry.Tile.LayoutJson),
                    run.Result,
                    run.Error));
            }

            return Results.Ok(new DashboardRefreshResponse(
                dashboard.Id, dashboard.Name, clock.GetUtcNow(), results));
        });

        return app;
    }

    private static async Task<DashboardTile?> FindTileAsync(
        PulseDbContext db,
        Guid projectId,
        Guid dashboardId,
        Guid tileId,
        CancellationToken ct)
    {
        var dashboardExists = await db.Dashboards
            .AnyAsync(d => d.ProjectId == projectId && d.Id == dashboardId, ct);

        if (!dashboardExists)
        {
            return null;
        }

        return await db.DashboardTiles
            .SingleOrDefaultAsync(t => t.DashboardId == dashboardId && t.Id == tileId, ct);
    }

    private static async Task<List<DashboardTileResponse>> LoadTilesAsync(
        PulseDbContext db,
        Guid dashboardId,
        CancellationToken ct)
    {
        var tiles = await (
            from t in db.DashboardTiles
            join i in db.Insights on t.InsightId equals i.Id
            where t.DashboardId == dashboardId
            orderby t.CreatedAt
            select new { Tile = t, Insight = i }).ToListAsync(ct);

        return tiles.Select(entry => ToTileResponse(entry.Tile, entry.Insight)).ToList();
    }

    private static DashboardTileResponse ToTileResponse(DashboardTile tile, Insight insight) =>
        new(
            tile.Id,
            insight.Id,
            insight.Name,
            insight.Type.ToString().ToLowerInvariant(),
            JsonSerializer.Deserialize<JsonElement>(tile.LayoutJson),
            tile.CreatedAt);

    private static DashboardResponse ToResponse(
        Dashboard dashboard,
        List<DashboardTileResponse> tiles) =>
        new(
            dashboard.Id,
            dashboard.ProjectId,
            dashboard.Name,
            dashboard.Description,
            tiles,
            dashboard.CreatedAt);
}
