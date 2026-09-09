using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class DashboardCopyTransactionTests
{
    [Fact]
    public async Task TemplateImportFailure_RollsBackTheEntireNewGraph()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_import_tile BEFORE INSERT ON DashboardTiles
            BEGIN SELECT RAISE(ABORT, 'injected import storage failure'); END;
            """);
        var document = new DashboardTemplate(1, "Import", "Rollback",
            [new TemplateInsight("q", "Signup", "trend", JsonSerializer.SerializeToElement(new { @event = "signup" }))],
            [new TemplateTile("q", JsonSerializer.SerializeToElement(new { x = 0 }))]);
        await Assert.ThrowsAsync<DbUpdateException>(() => new DashboardTemplateService(db, TimeProvider.System).ImportAsync(Guid.NewGuid(), document, default));
        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.Insights.CountAsync());
        Assert.Equal(0, await db.Dashboards.CountAsync());
        Assert.Equal(0, await db.DashboardTiles.CountAsync());
    }

    [Fact]
    public async Task FailedTileInsert_RollsBackCopiedDashboardAndTiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var project = new Project { Name = "Rollback", ApiKey = "pk_live_fixture", ReadKey = "rk_live_fixture" };
        var source = new Dashboard { ProjectId = project.Id, Name = "Source" };
        var insight = new Insight { ProjectId = project.Id, Name = "Shared" };
        db.Projects.Add(project);
        db.Dashboards.Add(source);
        db.Insights.Add(insight);
        db.DashboardTiles.AddRange(Enumerable.Range(0, 2).Select(_ => new DashboardTile { DashboardId = source.Id, InsightId = insight.Id }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_copied_tile BEFORE INSERT ON DashboardTiles
            BEGIN SELECT RAISE(ABORT, 'injected tile storage failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => new DashboardCopyService(db, TimeProvider.System)
            .CopyAsync(project.Id, source.Id, "Copy", default));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.Dashboards.CountAsync());
        Assert.Equal(source.Id, (await db.Dashboards.SingleAsync()).Id);
        Assert.Equal(2, await db.DashboardTiles.CountAsync());
        Assert.Equal(1, await db.Insights.CountAsync());
    }
}
