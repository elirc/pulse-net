using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class InsightEndpoints
{
    private static void MapInsightEditingFeatures(RouteGroupBuilder group)
    {
        group.MapPost("/preview", async (Guid projectId, PreviewInsightRequest request,
            HttpContext http, ProjectAccessService access, IInsightRunner runner, TimeProvider clock, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var candidate = InsightConfigValidator.Validate(request.Type, request.Config, clock.GetUtcNow());
            if (!candidate.IsValid) return Results.ValidationProblem(candidate.Errors);
            var temporary = new Insight { ProjectId = projectId, Name = "Preview", Type = candidate.Type, ConfigJson = candidate.RunJson };
            var run = await runner.RunAsync(temporary, ct);
            if (!run.Ok) return InputRules.Problem("config", run.Error ?? "Query could not execute.");
            return Results.Ok(new { type = candidate.Type.ToString().ToLowerInvariant(), result = run.Result });
        });

        group.MapPut("/{insightId:guid}", async (Guid projectId, Guid insightId, ReplaceInsightRequest request,
            HttpContext http, ProjectAccessService access, PulseDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var insight = await db.Insights.SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);
            if (insight is null) return Results.NotFound();
            var candidate = InsightConfigValidator.Validate(request.Type, request.Config, clock.GetUtcNow());
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) candidate.Errors["name"] = ["Name must contain 1–200 characters after trimming."];
            if (!candidate.IsValid) return Results.ValidationProblem(candidate.Errors);
            insight.Name = name!;
            insight.Type = candidate.Type;
            insight.ConfigJson = candidate.StorageJson;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(insight));
        });

        group.MapGet("/{insightId:guid}/usages", async (Guid projectId, Guid insightId,
            HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!await db.Insights.AnyAsync(i => i.ProjectId == projectId && i.Id == insightId, ct)) return Results.NotFound();
            return Results.Ok(await LoadUsagesAsync(db, projectId, insightId, ct));
        });

        group.MapDelete("/{insightId:guid}", async (Guid projectId, Guid insightId,
            HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var insight = await db.Insights.SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);
            if (insight is null) return Results.NotFound();
            if (await db.DashboardTiles.AnyAsync(t => t.InsightId == insightId, ct))
            {
                var usage = await LoadUsagesAsync(db, projectId, insightId, ct);
                return Results.Problem("Remove all referencing dashboard tiles before deleting this insight.", statusCode: 409,
                    extensions: new Dictionary<string, object?> { ["dashboardCount"] = usage.DashboardCount, ["tileCount"] = usage.TileCount });
            }
            db.Insights.Remove(insight);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private record DashboardUsage(Guid Id, string Name, int TileCount);
    private record InsightUsages(Guid InsightId, int DashboardCount, int TileCount, IReadOnlyList<DashboardUsage> Dashboards, bool Truncated);

    private static async Task<InsightUsages> LoadUsagesAsync(PulseDbContext db, Guid projectId, Guid insightId, CancellationToken ct)
    {
        var usage = from tile in db.DashboardTiles
                    join dashboard in db.Dashboards on tile.DashboardId equals dashboard.Id
                    where dashboard.ProjectId == projectId && tile.InsightId == insightId
                    select new { dashboard.Id, dashboard.Name };
        var tileCount = await usage.CountAsync(ct);
        var dashboardCount = await usage.Select(u => u.Id).Distinct().CountAsync(ct);
        var dashboards = await usage.GroupBy(u => new { u.Id, u.Name })
            .OrderBy(g => g.Key.Name).ThenBy(g => g.Key.Id).Take(100)
            .Select(g => new DashboardUsage(g.Key.Id, g.Key.Name, g.Count())).ToListAsync(ct);
        return new(insightId, dashboardCount, tileCount, dashboards, dashboardCount > dashboards.Count);
    }
}
