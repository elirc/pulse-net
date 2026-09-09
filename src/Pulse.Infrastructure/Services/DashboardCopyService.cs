using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record DashboardCopyResult(Dashboard? Dashboard, string? Error, bool Missing = false);

/// <summary>A copied dashboard owns new tiles but intentionally shares existing insights.</summary>
public sealed class DashboardCopyService(PulseDbContext db, TimeProvider clock)
{
    public async Task<DashboardCopyResult> CopyAsync(Guid projectId, Guid sourceId, string name, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await db.Dashboards.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == sourceId, ct);
        if (source is null) return new(null, null, Missing: true);
        var tiles = await db.DashboardTiles.AsNoTracking().Where(t => t.DashboardId == sourceId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(101).ToListAsync(ct);
        if (tiles.Count > 100) return new(null, "A dashboard copy supports at most 100 tiles.");
        var ids = tiles.Select(t => t.InsightId).Distinct().ToArray();
        var validIds = await db.Insights.Where(i => i.ProjectId == projectId && ids.Contains(i.Id))
            .Select(i => i.Id).ToListAsync(ct);
        if (validIds.Count != ids.Length) return new(null, "A source tile has an unavailable insight.");
        var now = clock.GetUtcNow();
        var copy = new Dashboard { ProjectId = projectId, Name = name, Description = source.Description, CreatedAt = now };
        db.Dashboards.Add(copy);
        db.DashboardTiles.AddRange(tiles.Select(t => new DashboardTile
        {
            DashboardId = copy.Id, InsightId = t.InsightId, LayoutJson = t.LayoutJson, CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(copy, null);
    }
}
