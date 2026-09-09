using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ExportUpgradeTests
{
    [Fact]
    public async Task Upgrade_PreservesTerminalDocumentsAndMakesLegacyRunningJobRecoverable()
    {
        await using var f = new IngestionReliabilityFixture(); await using var db = f.Open();
        await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("AddDurableIngestion", StringComparison.Ordinal)));
        var at = f.Clock.GetUtcNow();
        foreach (var status in new[] { "Running", "Completed", "Failed" })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExportJobs (Id, ProjectId, Type, Format, ParamsJson, Status, ResultContent, ContentType, RowCount, CreatedAt, CompletedAt, Error) VALUES ({Guid.NewGuid()}, {f.Project.Id}, {"events"}, {"json"}, {"{}"}, {status}, {"preserved"}, {"application/json"}, {3}, {at.UtcTicks}, {at.UtcTicks}, {"old error"})");
        await db.Database.MigrateAsync();
        var rows = await db.ExportJobs.AsNoTracking().ToListAsync();
        Assert.All(rows, row => { Assert.Equal("live", row.Consistency); Assert.Null(row.Owner); Assert.Null(row.LeaseExpiresAt); Assert.Equal(0, row.AttemptGeneration); Assert.False(row.SnapshotReady); });
        Assert.All(rows.Where(r => r.Status != ExportJobStatus.Running), row => { Assert.Equal("preserved", row.ResultContent); Assert.Equal(at, row.CompletedAt); Assert.Equal("old error", row.Error); });
        var running = rows.Single(r => r.Status == ExportJobStatus.Running);
        Assert.NotNull(await new ExportOwnershipService(db, f.Clock).ClaimAsync(f.Project.Id, running.Id, Guid.NewGuid(), default));
    }
}
