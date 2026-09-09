using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Pulse.Tests.Infrastructure;

public class IngestionTraceUpgradeTests
{
    [Fact]
    public async Task NullableTraceMigrationPreservesLegacyQueueAndDeadLetterValues()
    {
        await using var f = new IngestionReliabilityFixture(); await using var db = f.Open();
        await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("AddPersonErasure", StringComparison.Ordinal)));
        var project = Guid.NewGuid(); var letter = Guid.NewGuid(); var admission = Guid.NewGuid(); var now = f.Clock.Now;
        const string payload = "legacy payload preserved exactly";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO QueuedEvents (ProjectId, AdmissionId, PayloadJson, Attempts, EnqueuedAt, NextAttemptAt, LastErrorCode) VALUES ({project}, {admission}, {payload}, {2}, {now.UtcTicks}, {now.AddSeconds(2).UtcTicks}, {"sqlite_contention"})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DeadLetterEvents (Id, ProjectId, AdmissionId, PayloadJson, Error, Attempts, FailedAt) VALUES ({letter}, {project}, {admission}, {payload}, {"legacy failure"}, {5}, {now.UtcTicks})");
        await db.Database.MigrateAsync();
        var queued = await db.QueuedEvents.SingleAsync(); var failed = await db.DeadLetterEvents.SingleAsync();
        Assert.Equal(payload, queued.PayloadJson); Assert.Equal(payload, failed.PayloadJson);
        Assert.Equal(admission, queued.AdmissionId); Assert.Equal(admission, failed.AdmissionId);
        Assert.Equal(2, queued.Attempts); Assert.Equal(now.AddSeconds(2), queued.NextAttemptAt); Assert.Equal("sqlite_contention", queued.LastErrorCode);
        Assert.Equal(5, failed.Attempts); Assert.Equal("legacy failure", failed.Error); Assert.Equal(now, failed.FailedAt);
        Assert.Null(queued.TraceParent); Assert.Null(queued.OriginalTraceParent); Assert.Null(failed.TraceParent); Assert.Null(failed.OriginalTraceParent);
    }
}
