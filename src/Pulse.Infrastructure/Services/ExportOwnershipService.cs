using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public sealed class ExportWorkerIdentity { public Guid Owner { get; } = Guid.NewGuid(); }
public record ExportAttempt(Guid ProjectId, Guid JobId, Guid Owner, long Generation);
public record ExportCancelOutcome(int Status, ExportJobStatus? State = null);
public sealed class LostExportAttemptException() : Exception("The export attempt no longer has permission to publish.");

public sealed class ExportOwnershipService(PulseDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public async Task<ExportAttempt?> ClaimAsync(Guid projectId, Guid jobId, Guid owner, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await new QueueAdmissionService(db, new IngestionSignal(), clock).AcquireGateAsync(projectId, ct);
        if (settings.Paused) return null;
        var now = clock.GetUtcNow(); var expiry = now.Add(LeaseDuration);
        var claimable = db.ExportJobs.Where(j => j.ProjectId == projectId && j.Id == jobId && j.AttemptGeneration < long.MaxValue &&
            (j.Status == ExportJobStatus.Pending || (j.Status == ExportJobStatus.Running && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now))));
        var changed = await claimable.ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ExportJobStatus.Running).SetProperty(j => j.Owner, owner)
            .SetProperty(j => j.AttemptGeneration, j => j.AttemptGeneration + 1).SetProperty(j => j.LeaseExpiresAt, expiry).SetProperty(j => j.LastHeartbeatAt, now)
            .SetProperty(j => j.ResultContent, (string?)null).SetProperty(j => j.ContentType, (string?)null).SetProperty(j => j.RowCount, 0)
            .SetProperty(j => j.Error, (string?)null).SetProperty(j => j.CompletedAt, (DateTimeOffset?)null), ct);
        if (changed == 0) return null;
        var generation = await db.ExportJobs.Where(j => j.Id == jobId && j.ProjectId == projectId).Select(j => j.AttemptGeneration).SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return new(projectId, jobId, owner, generation);
    }

    public async Task<bool> HeartbeatAsync(ExportAttempt attempt, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow(); var expires = now.Add(LeaseDuration);
        var matching = db.ExportJobs.Where(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId && j.Owner == attempt.Owner && j.AttemptGeneration == attempt.Generation && j.LeaseExpiresAt > now);
        var state = await matching.Select(j => (ExportJobStatus?)j.Status).SingleOrDefaultAsync(ct);
        if (state == ExportJobStatus.CancelRequested)
        {
            await FinishCancellationAsync(attempt.ProjectId, attempt.JobId, ct);
            await transaction.CommitAsync(ct);
            return false;
        }
        if (await db.ProjectIngestionStates.AnyAsync(s => s.ProjectId == attempt.ProjectId && s.Paused, ct)) return false;
        var changed = await matching.Where(j => j.Status == ExportJobStatus.Running).ExecuteUpdateAsync(s => s.SetProperty(j => j.LastHeartbeatAt, now).SetProperty(j => j.LeaseExpiresAt, expires), ct);
        await transaction.CommitAsync(ct);
        return changed == 1;
    }

    public async Task FenceAsync(ExportAttempt attempt, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Export fencing needs a transaction.");
        var now = clock.GetUtcNow();
        if (await db.ProjectIngestionStates.AnyAsync(s => s.ProjectId == attempt.ProjectId && s.Paused, ct)) throw new LostExportAttemptException();
        var changed = await db.ExportJobs.Where(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId && j.Owner == attempt.Owner &&
            j.AttemptGeneration == attempt.Generation && j.Status == ExportJobStatus.Running && j.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AttemptGeneration, j => j.AttemptGeneration), ct);
        if (changed != 1) throw new LostExportAttemptException();
    }

    public async Task<bool> PublishAsync(ExportAttempt attempt, string? content, string? contentType, int rows, string? errorCode, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.ProjectIngestionStates.AnyAsync(s => s.ProjectId == attempt.ProjectId && s.Paused, ct)) return false;
        var now = clock.GetUtcNow(); var status = errorCode is null ? ExportJobStatus.Completed : ExportJobStatus.Failed;
        var changed = await db.ExportJobs.Where(j => j.ProjectId == attempt.ProjectId && j.Id == attempt.JobId && j.Owner == attempt.Owner &&
            j.AttemptGeneration == attempt.Generation && j.Status == ExportJobStatus.Running && j.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ResultContent, content).SetProperty(j => j.ContentType, contentType).SetProperty(j => j.RowCount, rows)
                .SetProperty(j => j.Error, errorCode).SetProperty(j => j.Status, status).SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.Owner, (Guid?)null).SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null), ct);
        await transaction.CommitAsync(ct);
        return changed == 1;
    }

    public async Task<ExportCancelOutcome> CancelAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.ExportJobs.AsNoTracking().Where(j => j.ProjectId == projectId && j.Id == jobId).Select(j => new { j.Status }).SingleOrDefaultAsync(ct);
        if (job is null) return new(404);
        if (job.Status is ExportJobStatus.Completed or ExportJobStatus.Failed) return new(409, job.Status);
        if (job.Status == ExportJobStatus.Pending) await FinishCancellationAsync(projectId, jobId, ct);
        if (job.Status == ExportJobStatus.Running)
            await db.ExportJobs.Where(j => j.ProjectId == projectId && j.Id == jobId && j.Status == ExportJobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ExportJobStatus.CancelRequested).SetProperty(j => j.ResultContent, (string?)null), ct);
        await transaction.CommitAsync(ct);
        var state = job.Status is ExportJobStatus.Pending or ExportJobStatus.Cancelled ? ExportJobStatus.Cancelled : ExportJobStatus.CancelRequested;
        return new(state == ExportJobStatus.Cancelled ? 200 : 202, state);
    }

    public async Task<int> RecoverCancelledAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await db.ExportJobs.AsNoTracking().Where(j => j.Status == ExportJobStatus.CancelRequested && (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now))
            .OrderBy(j => j.CreatedAt).Select(j => new { j.ProjectId, j.Id }).Take(20).ToListAsync(ct);
        var count = 0;
        foreach (var item in expired)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await db.ExportJobs.AnyAsync(j => j.Id == item.Id && j.ProjectId == item.ProjectId && j.Status == ExportJobStatus.CancelRequested &&
                (j.LeaseExpiresAt == null || j.LeaseExpiresAt <= now), ct)) continue;
            await FinishCancellationAsync(item.ProjectId, item.Id, ct); await transaction.CommitAsync(ct); count++;
        }
        return count;
    }

    private async Task FinishCancellationAsync(Guid projectId, Guid jobId, CancellationToken ct)
    {
        await db.ExportJobs.Where(j => j.ProjectId == projectId && j.Id == jobId && (j.Status == ExportJobStatus.Pending || j.Status == ExportJobStatus.CancelRequested))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ExportJobStatus.Cancelled).SetProperty(j => j.CompletedAt, clock.GetUtcNow())
                .SetProperty(j => j.ResultContent, (string?)null).SetProperty(j => j.ContentType, (string?)null).SetProperty(j => j.RowCount, 0)
                .SetProperty(j => j.Error, (string?)null).SetProperty(j => j.Owner, (Guid?)null).SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.SnapshotReady, false).SetProperty(j => j.SnapshotCapturedAt, (DateTimeOffset?)null), ct);
        await db.ExportSnapshotRows.Where(r => r.ProjectId == projectId && r.JobId == jobId).ExecuteDeleteAsync(ct);
    }
}
