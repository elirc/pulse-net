using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record RetentionPolicyOutcome(int Status, ProjectRetentionPolicy? Policy = null);
public record RetentionPreview(long Revision, bool Enabled, int Days, DateTimeOffset ObservedAt, DateTimeOffset Cutoff, long EligibleCount);

public sealed class EventRetentionService(PulseDbContext db, TimeProvider clock)
{
    public const int BatchSize = 1000;
    public async Task<ProjectRetentionPolicy> GetAsync(Guid projectId, CancellationToken ct) =>
        await db.ProjectRetentionPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.ProjectId == projectId, ct) ?? new() { ProjectId = projectId };

    public async Task<RetentionPolicyOutcome> UpdateAsync(Guid projectId, bool enabled, int days, long expectedRevision, CancellationToken ct)
    {
        if (days is < 30 or > 3650 || expectedRevision < 1 || expectedRevision == long.MaxValue) return new(400);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(projectId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProjectRetentionPolicies (ProjectId, Enabled, Days, Revision) VALUES ({projectId}, {false}, {365}, {1L}) ON CONFLICT(ProjectId) DO NOTHING", ct);
        var changed = await db.ProjectRetentionPolicies.Where(p => p.ProjectId == projectId && p.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, enabled).SetProperty(p => p.Days, days).SetProperty(p => p.Revision, p => p.Revision + 1), ct);
        if (changed == 0) return new(412);
        await db.RetentionRuns.Where(r => r.ProjectId == projectId && r.Status == RetentionRunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RetentionRunStatus.Superseded).SetProperty(r => r.CompletedAt, clock.GetUtcNow()), ct);
        var policy = await GetAsync(projectId, ct); await transaction.CommitAsync(ct); return new(200, policy);
    }

    public async Task<RetentionPreview> PreviewAsync(Guid projectId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var policy = await GetAsync(projectId, ct); var now = clock.GetUtcNow(); var cutoff = now.AddDays(-policy.Days);
        var count = await db.Events.LongCountAsync(e => e.ProjectId == projectId && e.Timestamp < cutoff, ct);
        await transaction.CommitAsync(ct); return new(policy.Revision, policy.Enabled, policy.Days, now, cutoff, count);
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        // Oldest serviced project first so a busy project cannot starve later IDs.
        var projects = await db.ProjectRetentionPolicies.AsNoTracking().Where(p => p.Enabled)
            .OrderBy(p => db.RetentionRuns.Where(r => r.ProjectId == p.ProjectId).Max(r => (DateTimeOffset?)r.LastBatchAt))
            .ThenBy(p => p.ProjectId).Select(p => p.ProjectId).Take(20).ToListAsync(ct);
        var removed = 0;
        foreach (var project in projects) removed += await ProcessBatchAsync(project, ct);
        return removed;
    }

    public async Task<int> ProcessBatchAsync(Guid projectId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(projectId, ct);
        if (settings.Paused) return 0;
        var now = clock.GetUtcNow(); var policy = await GetAsync(projectId, ct);
        var run = await db.RetentionRuns.AsNoTracking().SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Status == RetentionRunStatus.Running, ct);
        if (run is not null && (!policy.Enabled || run.PolicyRevision != policy.Revision))
        {
            await db.RetentionRuns.Where(r => r.Id == run.Id && r.ProjectId == projectId).ExecuteUpdateAsync(s =>
                s.SetProperty(r => r.Status, RetentionRunStatus.Superseded).SetProperty(r => r.CompletedAt, now), ct);
            await transaction.CommitAsync(ct); return 0;
        }
        if (!policy.Enabled) return 0;
        if (run is null)
        {
            run = new RetentionRun { ProjectId = projectId, PolicyRevision = policy.Revision, Cutoff = now.AddDays(-policy.Days), StartedAt = now };
            db.RetentionRuns.Add(run); await db.SaveChangesAsync(ct); db.Entry(run).State = EntityState.Detached;
        }
        var cutoff = run.Cutoff;
        var ids = await db.Events.Where(e => e.ProjectId == projectId && e.Timestamp < cutoff).OrderBy(e => e.Timestamp).ThenBy(e => e.Id)
            .Select(e => e.Id).Take(BatchSize).ToListAsync(ct);
        var removed = 0;
        if (ids.Count > 0)
        {
            await RetireEventPointersAsync(db, projectId, ids, "retention", ct);
            removed = await db.Events.Where(e => e.ProjectId == projectId && e.Timestamp < cutoff && ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
        }
        var complete = ids.Count < BatchSize;
        await db.RetentionRuns.Where(r => r.Id == run.Id && r.ProjectId == projectId && r.Status == RetentionRunStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RemovedCount, r => r.RemovedCount + removed).SetProperty(r => r.Batches, r => r.Batches + 1)
                .SetProperty(r => r.LastBatchAt, now).SetProperty(r => r.Status, complete ? RetentionRunStatus.Completed : RetentionRunStatus.Running)
                .SetProperty(r => r.CompletedAt, complete ? now : (DateTimeOffset?)null), ct);
        await transaction.CommitAsync(ct); return removed;
    }

    public static async Task RetireEventPointersAsync(PulseDbContext db, Guid projectId, IReadOnlyList<Guid> ids, string reason, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Pointer retirement must join data deletion.");
        await db.CaptureProcessingItems.Where(i => i.ProjectId == projectId && i.EventId != null && ids.Contains(i.EventId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EventId, (Guid?)null).SetProperty(i => i.RetiredReason, reason), ct);
        await db.CaptureItemTransitions.Where(i => i.ProjectId == projectId && i.EventId != null && ids.Contains(i.EventId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EventId, (Guid?)null), ct);
        // Outcome/generation/completion time remain unchanged: deletion never queues an event again.
    }
}
