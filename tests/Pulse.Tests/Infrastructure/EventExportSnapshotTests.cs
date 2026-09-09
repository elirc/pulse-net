using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class EventExportSnapshotTests
{
    [Fact]
    public async Task ReadySnapshot_SurvivesActualIdentityMergeSourceDeletionLateInsertAndOwnershipRecovery()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var db = f.Open();
        var identity = new IdentityService(db);
        var anonymous = await identity.ResolveAsync(f.Project.Id, "anon");
        var identified = await identity.ResolveAsync(f.Project.Id, "known");
        var at = f.Clock.GetUtcNow();
        var original = new AnalyticsEvent { ProjectId = f.Project.Id, PersonId = anonymous.Id, DistinctId = "anon", Name = "view", Timestamp = at, PropertiesJson = "{\"value\":1}" };
        var sourceToDelete = new AnalyticsEvent { ProjectId = f.Project.Id, PersonId = anonymous.Id, DistinctId = "anon", Name = "view", Timestamp = at };
        db.Events.AddRange(original, sourceToDelete, new AnalyticsEvent { ProjectId = Guid.NewGuid(), DistinctId = "foreign", Name = "view", Timestamp = at });
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Consistency = "snapshot" };
        db.ExportJobs.Add(job); await db.SaveChangesAsync();
        var ownership = new ExportOwnershipService(db, f.Clock);
        var attempt = (await ownership.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var snapshots = new ExportSnapshotService(db, ownership, f.Clock);
        await snapshots.CaptureAsync(attempt, "view", at, at, default);
        var before = JsonSerializer.Serialize(await snapshots.ReadPageAsync(attempt, 0, default));
        Assert.Equal(2, await db.ExportSnapshotRows.CountAsync());
        await identity.IdentifyAsync(f.Project.Id, "known", "anon"); await db.SaveChangesAsync();
        Assert.Equal(identified.Id, (await db.Events.AsNoTracking().SingleAsync(e => e.Id == original.Id)).PersonId);
        await db.Events.Where(e => e.Id == sourceToDelete.Id).ExecuteDeleteAsync();
        await db.Events.Where(e => e.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.PropertiesJson, "{\"value\":2}"));
        db.Events.Add(new() { ProjectId = f.Project.Id, DistinctId = "late", Name = "view", Timestamp = at }); await db.SaveChangesAsync();
        f.Clock.Advance(ExportOwnershipService.LeaseDuration);
        await using var recovered = f.Open();
        var nextOwnership = new ExportOwnershipService(recovered, f.Clock);
        var next = (await nextOwnership.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var nextSnapshots = new ExportSnapshotService(recovered, nextOwnership, f.Clock);
        await nextSnapshots.CaptureAsync(next, "view", at, at, default);
        var rows = await nextSnapshots.ReadPageAsync(next, 0, default);
        Assert.Equal(before, JsonSerializer.Serialize(rows));
        Assert.All(rows, row => Assert.Equal(anonymous.Id, row.PersonId));
        Assert.Equal(at, (await recovered.ExportJobs.AsNoTracking().SingleAsync()).SnapshotCapturedAt);
        Assert.False(await ownership.PublishAsync(attempt, "stale", "text/csv", 1, null, default));
        Assert.True(await nextOwnership.PublishAsync(next, before, "application/json", 2, null, default));
        Assert.Equal(204, await new ExportJobOperationsService(recovered, new(), f.Clock).DeleteAsync(f.Project.Id, job.Id, default));
        Assert.Empty(await recovered.ExportSnapshotRows.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverCap_RollsBackAllCopiedRowsAndReadiness(bool bytes)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using var db = f.Open(); var at = f.Clock.GetUtcNow();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Consistency = "snapshot" };
        db.ExportJobs.Add(job);
        if (bytes)
            db.Events.Add(new() { ProjectId = f.Project.Id, DistinctId = "p", Name = "big", Timestamp = at, PropertiesJson = JsonSerializer.Serialize(new { value = new string('x', ExportSnapshotService.MaxBytes) }) });
        else
            db.Events.AddRange(Enumerable.Range(0, ExportSnapshotService.MaxRows + 1).Select(_ => new AnalyticsEvent { ProjectId = f.Project.Id, DistinctId = "p", Name = "many", Timestamp = at }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var ownership = new ExportOwnershipService(db, f.Clock);
        var attempt = (await ownership.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var error = await Assert.ThrowsAsync<ExportSnapshotLimitException>(() => new ExportSnapshotService(db, ownership, f.Clock).CaptureAsync(attempt, null, at, at, default));
        Assert.Equal(bytes ? "snapshot_byte_limit_exceeded" : "snapshot_row_limit_exceeded", error.Code);
        Assert.Empty(await db.ExportSnapshotRows.ToListAsync());
        var saved = await db.ExportJobs.AsNoTracking().SingleAsync(); Assert.False(saved.SnapshotReady); Assert.Null(saved.SnapshotCapturedAt);
    }

    [Fact]
    public async Task EmptySnapshot_IsReadyAndCancelRemovesReadiness()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Consistency = "snapshot" };
        db.ExportJobs.Add(job); await db.SaveChangesAsync();
        var ownership = new ExportOwnershipService(db, f.Clock); var attempt = (await ownership.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var snapshots = new ExportSnapshotService(db, ownership, f.Clock); var at = f.Clock.GetUtcNow();
        await snapshots.CaptureAsync(attempt, null, at, at, default);
        Assert.True((await db.ExportJobs.AsNoTracking().SingleAsync()).SnapshotReady);
        Assert.Empty(await snapshots.ReadPageAsync(attempt, 0, default));
        await ownership.CancelAsync(f.Project.Id, job.Id, default); Assert.False(await ownership.HeartbeatAsync(attempt, default));
        Assert.False((await db.ExportJobs.AsNoTracking().SingleAsync()).SnapshotReady);
        await Assert.ThrowsAsync<LostExportAttemptException>(() => snapshots.CaptureAsync(attempt, null, at, at, default));
    }

    [Fact]
    public async Task SaveFailure_HasNoPartialRowsOrReadyMarker()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        Guid id; var at = f.Clock.GetUtcNow();
        await using (var setup = f.Open())
        {
            var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Consistency = "snapshot" }; id = job.Id;
            setup.ExportJobs.Add(job); setup.Events.Add(new() { ProjectId = f.Project.Id, Name = "a", DistinctId = "p", Timestamp = at }); await setup.SaveChangesAsync();
        }
        await using (var failing = f.Open(new FailSnapshotSave()))
        {
            var owner = new ExportOwnershipService(failing, f.Clock); var attempt = (await owner.ClaimAsync(f.Project.Id, id, Guid.NewGuid(), default))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ExportSnapshotService(failing, owner, f.Clock).CaptureAsync(attempt, null, at, at, default));
        }
        await using var verify = f.Open(); Assert.Empty(await verify.ExportSnapshotRows.ToListAsync()); Assert.False((await verify.ExportJobs.SingleAsync()).SnapshotReady);
    }

    private sealed class FailSnapshotSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ExportSnapshotRow>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("injected snapshot failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
