using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class EventRetentionTests
{
    [Fact]
    public async Task PreviewAndDeletion_SelectIdsAndCountsWithoutMaterializingProperties()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using (var setup = f.Open())
        {
            setup.Events.Add(new() { ProjectId = f.Project.Id, Name = "old", DistinctId = "p", Timestamp = f.Clock.GetUtcNow().AddDays(-31), PropertiesJson = "{\"secret\":\"value\"}" });
            await setup.SaveChangesAsync(); await new EventRetentionService(setup, f.Clock).UpdateAsync(f.Project.Id, true, 30, 1, default);
        }
        var reads = new RecordEventReads(); await using var db = f.Open(reads); var service = new EventRetentionService(db, f.Clock);
        Assert.Equal(1, (await service.PreviewAsync(f.Project.Id, default)).EligibleCount);
        Assert.Equal(1, await service.ProcessBatchAsync(f.Project.Id, default));
        Assert.NotEmpty(reads.Commands); Assert.All(reads.Commands, sql => Assert.DoesNotContain("PropertiesJson", sql));
    }

    [Fact]
    public async Task DisabledDefaultAndPreview_DoNotDelete_ThenFixedCutoffSurvivesRestartAcrossBatches()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        var now = f.Clock.GetUtcNow(); var cutoff = now.AddDays(-30); var foreign = Guid.NewGuid();
        var atBoundary = new AnalyticsEvent { ProjectId = f.Project.Id, Name = "boundary", DistinctId = "p", Timestamp = cutoff };
        db.Events.AddRange(Enumerable.Range(0, 1001).Select(_ => new AnalyticsEvent { ProjectId = f.Project.Id, Name = "old", DistinctId = "p", Timestamp = cutoff.AddTicks(-1), PropertiesJson = "{\"private\":\"not needed to delete\"}" }));
        db.Events.AddRange(atBoundary, new() { ProjectId = foreign, Name = "foreign", DistinctId = "p", Timestamp = cutoff.AddDays(-5) });
        db.Persons.Add(new() { ProjectId = f.Project.Id }); db.ExportJobs.Add(new() { ProjectId = f.Project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Completed, ResultContent = "keep" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = new EventRetentionService(db, f.Clock);
        Assert.False((await service.GetAsync(f.Project.Id, default)).Enabled);
        Assert.Equal(0, await service.ProcessBatchAsync(f.Project.Id, default)); Assert.Equal(1003, await db.Events.CountAsync());
        Assert.Equal(200, (await service.UpdateAsync(f.Project.Id, true, 30, 1, default)).Status);
        var preview = await service.PreviewAsync(f.Project.Id, default); Assert.Equal(1001, preview.EligibleCount); Assert.Equal(cutoff, preview.Cutoff);
        Assert.Equal(1000, await service.ProcessBatchAsync(f.Project.Id, default));
        var run = await db.RetentionRuns.AsNoTracking().SingleAsync(); Assert.Equal(RetentionRunStatus.Running, run.Status); Assert.Equal(1000, run.RemovedCount);
        f.Clock.Advance(TimeSpan.FromDays(1));
        await using var restarted = f.Open(); var resumed = new EventRetentionService(restarted, f.Clock);
        Assert.Equal(1, await resumed.ProcessBatchAsync(f.Project.Id, default));
        run = await restarted.RetentionRuns.AsNoTracking().SingleAsync(); Assert.Equal(cutoff, run.Cutoff); Assert.Equal(1001, run.RemovedCount); Assert.Equal(2, run.Batches); Assert.Equal(RetentionRunStatus.Completed, run.Status);
        Assert.True(await restarted.Events.AnyAsync(e => e.Id == atBoundary.Id)); Assert.True(await restarted.Events.AnyAsync(e => e.ProjectId == foreign));
        Assert.Single(await restarted.Persons.ToListAsync()); Assert.Equal("keep", (await restarted.ExportJobs.SingleAsync()).ResultContent);
        // A later run has a later horizon; the prior run did not move its own cutoff.
        Assert.Equal(1, await resumed.SweepAsync(default)); Assert.Equal(2, await restarted.RetentionRuns.CountAsync());
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 365)]
    public async Task PolicyChange_SupersedesOldRunAndStaleEditsCannotRestoreShorterPeriod(bool enabled, int days)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        db.Events.AddRange(Enumerable.Range(0, 1001).Select(_ => new AnalyticsEvent { ProjectId = f.Project.Id, Name = "old", DistinctId = "p", Timestamp = f.Clock.GetUtcNow().AddDays(-31) })); await db.SaveChangesAsync();
        var service = new EventRetentionService(db, f.Clock); await service.UpdateAsync(f.Project.Id, true, 30, 1, default);
        Assert.Equal(1000, await service.ProcessBatchAsync(f.Project.Id, default));
        Assert.Equal(200, (await service.UpdateAsync(f.Project.Id, enabled, days, 2, default)).Status);
        Assert.Equal(412, (await service.UpdateAsync(f.Project.Id, true, 30, 2, default)).Status);
        Assert.Equal(0, await service.ProcessBatchAsync(f.Project.Id, default)); Assert.Equal(1, await db.Events.CountAsync());
        var original = await db.RetentionRuns.AsNoTracking().SingleAsync(r => r.PolicyRevision == 2); Assert.Equal(RetentionRunStatus.Superseded, original.Status); Assert.Equal(1000, original.RemovedCount);
    }

    [Fact]
    public async Task RetentionRetiresPointersAtomically_WithoutChangingProcessedOutcomeOrCompletionTime()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        await f.Admission(db).AdmitAsync(f.Project.Id, [new("old", "p", f.Clock.GetUtcNow().AddDays(-31), "{}")], true, default);
        Assert.Equal(1, (await f.Processor(db).ProcessPendingAsync()).Processed);
        var receipt = await db.CaptureReceipts.SingleAsync();
        var before = (await f.Receipts(db).ReadAsync(f.Project.Id, receipt.Id, default))!;
        var service = new EventRetentionService(db, f.Clock); await service.UpdateAsync(f.Project.Id, true, 30, 1, default);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_retention_progress BEFORE UPDATE ON RetentionRuns BEGIN SELECT RAISE(ABORT, 'injected retention failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => service.ProcessBatchAsync(f.Project.Id, default));
        await using (var verify = f.Open())
        {
            Assert.Single(await verify.Events.ToListAsync()); Assert.Empty(await verify.RetentionRuns.ToListAsync());
            Assert.NotNull((await verify.CaptureProcessingItems.SingleAsync()).EventId);
        }
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_retention_progress;");
        db.ChangeTracker.Clear(); Assert.Equal(1, await service.ProcessBatchAsync(f.Project.Id, default));
        var after = (await f.Receipts(db).ReadAsync(f.Project.Id, receipt.Id, default))!;
        Assert.Equal(before.CompletedAt, after.CompletedAt); Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        var item = Assert.Single(after.Items); Assert.Equal("processed", item.State); Assert.Equal("retention", item.RetiredReason); Assert.Null(item.EventId);
        Assert.All(await db.CaptureItemTransitions.ToListAsync(), transition => Assert.Null(transition.EventId));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(3651)]
    public async Task InvalidDays_DoNotCreateOrChangePolicy(int days)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        Assert.Equal(400, (await new EventRetentionService(db, f.Clock).UpdateAsync(f.Project.Id, true, days, 1, default)).Status);
        Assert.Empty(await db.ProjectRetentionPolicies.ToListAsync());
    }

    [Fact]
    public async Task PolicyEditWaitsForCommittedBatch_ThenStopsRemainingOldPolicyWork()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using (var setup = f.Open())
        {
            setup.Events.AddRange(Enumerable.Range(0, 1001).Select(_ => new AnalyticsEvent { ProjectId = f.Project.Id, Name = "old", DistinctId = "p", Timestamp = f.Clock.GetUtcNow().AddDays(-31) })); await setup.SaveChangesAsync();
            await new EventRetentionService(setup, f.Clock).UpdateAsync(f.Project.Id, true, 30, 1, default);
        }
        var barrier = new BlockDeletion();
        var batch = Task.Run(async () => { await using var db = f.Open(barrier); return await new EventRetentionService(db, f.Clock).ProcessBatchAsync(f.Project.Id, default); });
        Task<RetentionPolicyOutcome>? edit = null;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(90));
            edit = Task.Run(async () => { await using var db = f.Open(); return await new EventRetentionService(db, f.Clock).UpdateAsync(f.Project.Id, false, 30, 2, default); });
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1000, await batch.WaitAsync(TimeSpan.FromSeconds(90))); Assert.Equal(200, (await edit!.WaitAsync(TimeSpan.FromSeconds(90))).Status);
        await using var verify = f.Open(); Assert.Equal(0, await new EventRetentionService(verify, f.Clock).ProcessBatchAsync(f.Project.Id, default));
        Assert.Equal(1, await verify.Events.CountAsync()); Assert.Equal(RetentionRunStatus.Superseded, (await verify.RetentionRuns.SingleAsync()).Status);
    }

    private sealed class BlockDeletion : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM \"Events\"", StringComparison.Ordinal)) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private sealed class RecordEventReads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Events\"", StringComparison.Ordinal)) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
