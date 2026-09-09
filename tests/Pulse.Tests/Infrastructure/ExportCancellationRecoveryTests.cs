using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ExportCancellationRecoveryTests
{
    [Fact]
    public async Task PendingCancellation_IsImmediateRepeatableScopedAndDeletesCopiedInputs()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var db = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", ResultContent = "unpublished" };
        db.ExportJobs.Add(job); db.ExportSnapshotRows.Add(new() { ProjectId = f.Project.Id, JobId = job.Id, Ordinal = 0, RowJson = "{}" });
        await db.SaveChangesAsync();
        var service = new ExportOwnershipService(db, f.Clock);
        Assert.Equal(404, (await service.CancelAsync(Guid.NewGuid(), job.Id, default)).Status);
        Assert.Equal(new ExportCancelOutcome(200, ExportJobStatus.Cancelled), await service.CancelAsync(f.Project.Id, job.Id, default));
        Assert.Equal(200, (await service.CancelAsync(f.Project.Id, job.Id, default)).Status);
        Assert.Null(await service.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default));
        var saved = await db.ExportJobs.AsNoTracking().SingleAsync();
        Assert.Null(saved.ResultContent); Assert.Null(saved.Owner); Assert.Equal(f.Clock.GetUtcNow(), saved.CompletedAt);
        Assert.Empty(await db.ExportSnapshotRows.ToListAsync());
    }

    [Fact]
    public async Task ExpiredRunningAttempt_IsReclaimedAndOldOwnerCannotHeartbeatOrPublish()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var a = f.Open(); await using var b = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Running };
        a.ExportJobs.Add(job); await a.SaveChangesAsync();
        var first = new ExportOwnershipService(a, f.Clock); var second = new ExportOwnershipService(b, f.Clock);
        var old = (await first.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        Assert.Equal(1, old.Generation);
        Assert.Null(await second.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default));
        f.Clock.Advance(ExportOwnershipService.LeaseDuration);
        var current = (await second.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        Assert.Equal(2, current.Generation);
        Assert.False(await first.HeartbeatAsync(old, default));
        Assert.False(await first.PublishAsync(old, "stale", "text/csv", 1, null, default));
        Assert.True(await second.PublishAsync(current, "current", "text/csv", 1, null, default));
        Assert.Equal(409, (await first.CancelAsync(f.Project.Id, job.Id, default)).Status);
        Assert.False(await first.PublishAsync(old, null, null, 0, "stale_failure", default));
        Assert.Equal("current", (await b.ExportJobs.AsNoTracking().SingleAsync()).ResultContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningCancellation_PreventsPublicationAndFinalizesByHeartbeatOrRecovery(bool abandon)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var db = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json" };
        db.ExportJobs.Add(job); await db.SaveChangesAsync();
        var service = new ExportOwnershipService(db, f.Clock);
        var attempt = (await service.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        Assert.Equal(new ExportCancelOutcome(202, ExportJobStatus.CancelRequested), await service.CancelAsync(f.Project.Id, job.Id, default));
        Assert.Equal(202, (await service.CancelAsync(f.Project.Id, job.Id, default)).Status);
        Assert.False(await service.PublishAsync(attempt, "late", "application/json", 1, null, default));
        if (abandon)
        {
            Assert.Equal(0, await service.RecoverCancelledAsync(default));
            f.Clock.Advance(ExportOwnershipService.LeaseDuration);
            Assert.Equal(1, await service.RecoverCancelledAsync(default));
        }
        else Assert.False(await service.HeartbeatAsync(attempt, default));
        var saved = await db.ExportJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ExportJobStatus.Cancelled, saved.Status); Assert.Null(saved.ResultContent); Assert.Null(saved.Error);
        Assert.Equal(0, await service.RecoverCancelledAsync(default));
    }

    [Fact]
    public async Task CompetingCancelAndPublish_ProduceExactlyOneValidTerminalOutcome()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var setup = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json" };
        setup.ExportJobs.Add(job); await setup.SaveChangesAsync();
        var attempt = (await new ExportOwnershipService(setup, f.Clock).ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = Task.Run(async () => { await start.Task; await using var db = f.Open(); return await new ExportOwnershipService(db, f.Clock).PublishAsync(attempt, "winner", "text/csv", 1, null, default); });
        var cancel = Task.Run(async () => { await start.Task; await using var db = f.Open(); return await new ExportOwnershipService(db, f.Clock).CancelAsync(f.Project.Id, job.Id, default); });
        start.SetResult(); await Task.WhenAll(publish, cancel);
        var published = await publish; var cancellation = await cancel;
        var saved = await setup.ExportJobs.AsNoTracking().SingleAsync();
        if (published) { Assert.Equal(409, cancellation.Status); Assert.Equal(ExportJobStatus.Completed, saved.Status); Assert.Equal("winner", saved.ResultContent); }
        else { Assert.Equal(202, cancellation.Status); Assert.Equal(ExportJobStatus.CancelRequested, saved.Status); Assert.Null(saved.ResultContent); }
    }

    [Fact]
    public async Task MaintenancePause_PreventsClaimAndLatePublication()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var db = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json" };
        db.ExportJobs.Add(job); await db.SaveChangesAsync();
        var service = new ExportOwnershipService(db, f.Clock);
        var attempt = (await service.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        await db.ProjectIngestionStates.Where(s => s.ProjectId == f.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Paused, true));
        Assert.False(await service.PublishAsync(attempt, "private", "text/csv", 1, null, default));
        Assert.False(await service.HeartbeatAsync(attempt, default));
        f.Clock.Advance(ExportOwnershipService.LeaseDuration);
        Assert.Null(await service.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default));
    }
}
