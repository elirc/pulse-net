using System.Text;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record ErasureOutcome(int Status, ErasureJob? Job = null, string? Code = null);
public record ErasureDiscardSelection(long? QueueSequence, Guid? DeadLetterId, string? ContentHash);

public sealed class PersonErasureService(PulseDbContext db, SuppressionKeyRing keys, TimeProvider clock)
{
    public const int MaxAliases = 10000;
    private const int EnvelopeBatchSize = 100;
    private const int EnvelopeBatchBytes = 8 * 1024 * 1024;

    public async Task<ErasureOutcome> InitiateAsync(Guid projectId, Guid personId, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(projectId, ct);
        if (!await IsAdminAsync(projectId, actorId, ct)) return new(403);
        if (state.Paused)
        {
            var active = await db.ErasureJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == state.ErasureJobId, ct);
            return active?.PersonId == personId ? new(202, active) : new(409, Code: "project_already_paused");
        }
        if (!await db.Persons.AnyAsync(p => p.ProjectId == projectId && p.Id == personId, ct)) return new(404);
        keys.Require(keys.CurrentVersion);
        var aliases = await db.PersonDistinctIds.Where(m => m.ProjectId == projectId && m.PersonId == personId).Select(m => m.DistinctId)
            .Union(db.Events.Where(e => e.ProjectId == projectId && e.PersonId == personId).Select(e => e.DistinctId)).Take(MaxAliases + 1).ToListAsync(ct);
        if (aliases.Count > MaxAliases) return new(422, Code: "erasure_identity_limit_exceeded");
        var hashes = aliases.Select(alias => keys.Fingerprint(projectId, alias, keys.CurrentVersion)).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.IdentitySuppressions.Where(s => s.ProjectId == projectId && s.KeyVersion == keys.CurrentVersion && hashes.Contains(s.Fingerprint)).Select(s => s.Fingerprint).ToHashSetAsync(ct);
        var now = clock.GetUtcNow(); var generation = checked(state.MaintenanceGeneration + 1);
        var job = new ErasureJob { ProjectId = projectId, PersonId = personId, RequestedBy = actorId, CreatedAt = now, UpdatedAt = now, MaintenanceGeneration = generation };
        db.ErasureJobs.Add(job);
        db.ErasureIdentities.AddRange(hashes.Select(hash => new ErasureIdentity { JobId = job.Id, KeyVersion = keys.CurrentVersion, Fingerprint = hash }));
        db.IdentitySuppressions.AddRange(hashes.Where(hash => !existing.Contains(hash)).Select(hash => new IdentitySuppression
            { ProjectId = projectId, KeyVersion = keys.CurrentVersion, Fingerprint = hash, CreatedAt = now }));
        await db.ProjectIngestionStates.Where(s => s.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Paused, true)
            .SetProperty(p => p.MaintenanceGeneration, generation).SetProperty(p => p.ErasureJobId, job.Id), ct);
        await db.ProjectIngestionLeases.Where(l => l.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, (Guid?)null)
            .SetProperty(l => l.Generation, l => l.Generation == long.MaxValue ? long.MaxValue : l.Generation + 1)
            .SetProperty(l => l.ExpiresAt, DateTimeOffset.MinValue), ct);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(202, job);
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var jobs = await db.ErasureJobs.AsNoTracking().Where(j => j.Status == ErasureJobStatus.Pending || j.Status == ErasureJobStatus.Running)
            .OrderBy(j => j.UpdatedAt).ThenBy(j => j.Id).Select(j => j.Id).Take(10).ToListAsync(ct);
        foreach (var id in jobs) await ProcessAsync(id, ct);
        return jobs.Count;
    }

    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        try { await ProcessBatchAsync(jobId, ct); }
        catch (OperationCanceledException) { db.ChangeTracker.Clear(); throw; }
        catch
        {
            db.ChangeTracker.Clear();
            // Failure is durable and inspectable. Only an explicit admin resume starts it again.
            await db.ErasureJobs.Where(j => j.Id == jobId && (j.Status == ErasureJobStatus.Pending || j.Status == ErasureJobStatus.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ErasureJobStatus.Failed).SetProperty(j => j.ErrorCode, "erasure_processing_failed")
                    .SetProperty(j => j.UpdatedAt, clock.GetUtcNow()), ct);
            throw;
        }
    }

    private async Task ProcessBatchAsync(Guid jobId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.ErasureJobs.SingleOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is not (ErasureJobStatus.Pending or ErasureJobStatus.Running)) return;
        var state = await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(job.ProjectId, ct);
        if (!state.Paused || state.ErasureJobId != job.Id || state.MaintenanceGeneration != job.MaintenanceGeneration)
            throw new InvalidOperationException("Erasure maintenance ownership changed.");
        var frozen = await db.ErasureIdentities.AsNoTracking().Where(i => i.JobId == job.Id).ToListAsync(ct);
        foreach (var version in frozen.Select(i => i.KeyVersion).Distinct()) keys.Require(version);
        var sets = frozen.GroupBy(i => i.KeyVersion).ToDictionary(g => g.Key, g => g.Select(i => i.Fingerprint).ToHashSet(StringComparer.Ordinal));
        bool Matches(string identity) => sets.Any(set => set.Value.Contains(keys.Fingerprint(job.ProjectId, identity, set.Key)));
        job.Status = ErasureJobStatus.Running; job.UpdatedAt = clock.GetUtcNow();
        switch (job.Phase)
        {
            case ErasurePhase.Exports:
            {
                var ids = await db.ExportJobs.Where(j => j.ProjectId == job.ProjectId && j.InvalidatedByErasureJobId != job.Id).OrderBy(j => j.Id).Select(j => j.Id).Take(20).ToListAsync(ct);
                if (ids.Count == 0) { job.Phase = ErasurePhase.SnapshotCopies; break; }
                job.InvalidatedExports += await db.ExportJobs.Where(j => j.ProjectId == job.ProjectId && ids.Contains(j.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, ExportJobStatus.Cancelled).SetProperty(j => j.Owner, (Guid?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(j => j.ResultContent, (string?)null).SetProperty(j => j.ContentType, (string?)null)
                        .SetProperty(j => j.RowCount, 0).SetProperty(j => j.Error, "erasure_invalidated").SetProperty(j => j.CompletedAt, job.UpdatedAt)
                        .SetProperty(j => j.SnapshotReady, false).SetProperty(j => j.SnapshotCapturedAt, (DateTimeOffset?)null)
                        .SetProperty(j => j.InvalidatedByErasureJobId, job.Id), ct);
                break;
            }
            case ErasurePhase.SnapshotCopies:
            {
                var rows = await db.ExportSnapshotRows.AsNoTracking().Where(r => r.ProjectId == job.ProjectId).OrderBy(r => r.JobId).ThenBy(r => r.Ordinal)
                    .Select(r => new ExportSnapshotRow { JobId = r.JobId, Ordinal = r.Ordinal, ProjectId = r.ProjectId, RowJson = "" }).Take(1000).ToListAsync(ct);
                db.ExportSnapshotRows.RemoveRange(rows);
                if (rows.Count < 1000) job.Phase = ErasurePhase.Events;
                break;
            }
            case ErasurePhase.Events:
            {
                var cursor = job.EventCursor;
                var rows = await db.Events.Where(e => e.ProjectId == job.ProjectId && (cursor == null || e.Id.CompareTo(cursor.Value) > 0))
                    .OrderBy(e => e.Id).Select(e => new { e.Id, e.PersonId, e.DistinctId }).Take(1000).ToListAsync(ct);
                var ids = rows.Where(e => e.PersonId == job.PersonId || Matches(e.DistinctId)).Select(e => e.Id).ToList();
                if (ids.Count > 0)
                {
                    await EventRetentionService.RetireEventPointersAsync(db, job.ProjectId, ids, "erasure", ct);
                    job.RemovedEvents += await db.Events.Where(e => e.ProjectId == job.ProjectId && ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
                }
                if (rows.Count > 0) job.EventCursor = rows[^1].Id;
                if (rows.Count < 1000) job.Phase = ErasurePhase.Queue;
                break;
            }
            case ErasurePhase.Queue:
            {
                var rows = new List<QueuedEvent>(); long bytes = 0;
                await foreach (var row in db.QueuedEvents.AsNoTracking().Where(q => q.ProjectId == job.ProjectId && q.Seq > job.QueueCursor).OrderBy(q => q.Seq).Take(EnvelopeBatchSize).AsAsyncEnumerable().WithCancellation(ct))
                { rows.Add(row); bytes += Encoding.UTF8.GetByteCount(row.PayloadJson); if (bytes >= EnvelopeBatchBytes) break; }
                foreach (var row in rows)
                {
                    job.QueueCursor = row.Seq;
                    if (!IdentitySuppressionService.TryClassify(row.PayloadJson, out var incoming))
                    {
                        db.ErasureUnreadableItems.Add(new() { JobId = job.Id, ProjectId = job.ProjectId, QueueSequence = row.Seq, ContentHash = IdentitySuppressionService.ContentHash(row.PayloadJson), ObservedAt = job.UpdatedAt });
                        job.Status = ErasureJobStatus.NeedsReview; continue;
                    }
                    if (!IdentitySuppressionService.Identities(incoming!).Any(Matches)) continue;
                    await SuppressItemAsync(job.ProjectId, row.AdmissionId, "erasure", ct);
                    job.RemovedQueueItems += await db.QueuedEvents.Where(q => q.ProjectId == job.ProjectId && q.Seq == row.Seq).ExecuteDeleteAsync(ct);
                }
                if (rows.Count == 0) job.Phase = ErasurePhase.DeadLetters;
                break;
            }
            case ErasurePhase.DeadLetters:
            {
                var cursor = job.DeadLetterCursor; var rows = new List<DeadLetterEvent>(); long bytes = 0;
                await foreach (var row in db.DeadLetterEvents.AsNoTracking().Where(d => d.ProjectId == job.ProjectId && (cursor == null || d.Id.CompareTo(cursor.Value) > 0))
                    .OrderBy(d => d.Id).Take(EnvelopeBatchSize).AsAsyncEnumerable().WithCancellation(ct))
                { rows.Add(row); bytes += Encoding.UTF8.GetByteCount(row.PayloadJson); if (bytes >= EnvelopeBatchBytes) break; }
                foreach (var row in rows)
                {
                    job.DeadLetterCursor = row.Id;
                    if (!IdentitySuppressionService.TryClassify(row.PayloadJson, out var incoming))
                    {
                        db.ErasureUnreadableItems.Add(new() { JobId = job.Id, ProjectId = job.ProjectId, DeadLetterId = row.Id, ContentHash = IdentitySuppressionService.ContentHash(row.PayloadJson), ObservedAt = job.UpdatedAt });
                        job.Status = ErasureJobStatus.NeedsReview; continue;
                    }
                    if (!IdentitySuppressionService.Identities(incoming!).Any(Matches)) continue;
                    await SuppressItemAsync(job.ProjectId, row.AdmissionId, "erasure", ct);
                    await RetireLetterPointersAsync(job.ProjectId, row.Id, ct);
                    job.RemovedDeadLetters += await db.DeadLetterEvents.Where(d => d.ProjectId == job.ProjectId && d.Id == row.Id).ExecuteDeleteAsync(ct);
                }
                if (rows.Count == 0) job.Phase = ErasurePhase.CohortLinks;
                break;
            }
            case ErasurePhase.CohortLinks:
            {
                var rows = await db.CohortPersons.Where(cp => cp.PersonId == job.PersonId && db.Cohorts.Any(c => c.Id == cp.CohortId && c.ProjectId == job.ProjectId)).Take(1000).ToListAsync(ct);
                db.CohortPersons.RemoveRange(rows); job.RemovedCohortLinks += rows.Count;
                if (rows.Count < 1000) job.Phase = ErasurePhase.Aliases;
                break;
            }
            case ErasurePhase.Aliases:
            {
                var ids = await db.PersonDistinctIds.Where(m => m.ProjectId == job.ProjectId && m.PersonId == job.PersonId).Select(m => m.Id).Take(1000).ToListAsync(ct);
                job.RemovedAliases += await db.PersonDistinctIds.Where(m => m.ProjectId == job.ProjectId && ids.Contains(m.Id)).ExecuteDeleteAsync(ct);
                if (ids.Count < 1000) job.Phase = ErasurePhase.Person;
                break;
            }
            case ErasurePhase.Person:
                await db.Persons.Where(p => p.ProjectId == job.ProjectId && p.Id == job.PersonId).ExecuteDeleteAsync(ct);
                job.Phase = ErasurePhase.Verify; break;
            case ErasurePhase.Verify:
            {
                if (await db.Persons.AnyAsync(p => p.ProjectId == job.ProjectId && p.Id == job.PersonId, ct) ||
                    await db.PersonDistinctIds.AnyAsync(m => m.ProjectId == job.ProjectId && m.PersonId == job.PersonId, ct) ||
                    await db.Events.AnyAsync(e => e.ProjectId == job.ProjectId && e.PersonId == job.PersonId, ct) ||
                    await db.CohortPersons.AnyAsync(cp => cp.PersonId == job.PersonId && db.Cohorts.Any(c => c.Id == cp.CohortId && c.ProjectId == job.ProjectId), ct) ||
                    await db.ExportJobs.AnyAsync(j => j.ProjectId == job.ProjectId && (j.InvalidatedByErasureJobId != job.Id || j.ResultContent != null), ct) ||
                    await db.ExportSnapshotRows.AnyAsync(r => r.ProjectId == job.ProjectId, ct) ||
                    await db.ErasureUnreadableItems.AnyAsync(i => i.JobId == job.Id && i.DiscardedAt == null, ct)) throw new InvalidOperationException("Erasure inventory is incomplete.");
                foreach (var set in sets)
                    if (await db.IdentitySuppressions.CountAsync(s => s.ProjectId == job.ProjectId && s.KeyVersion == set.Key && set.Value.Contains(s.Fingerprint), ct) != set.Value.Count)
                        throw new InvalidOperationException("Erasure suppression inventory is incomplete.");
                job.Status = ErasureJobStatus.Completed; job.CompletedAt = job.UpdatedAt;
                await db.ProjectIngestionStates.Where(s => s.ProjectId == job.ProjectId && s.ErasureJobId == job.Id && s.MaintenanceGeneration == job.MaintenanceGeneration)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Paused, false).SetProperty(p => p.ErasureJobId, (Guid?)null), ct);
                break;
            }
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); db.ChangeTracker.Clear();
    }

    public async Task<ErasureOutcome> ResumeAsync(Guid projectId, Guid jobId, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(projectId, ct);
        if (!await IsAdminAsync(projectId, actorId, ct)) return new(403);
        var job = await db.ErasureJobs.SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);
        if (job is null) return new(404);
        if (job.Status != ErasureJobStatus.Failed) return new(409, Code: "only_failed_jobs_can_resume");
        if (!OwnsPause(state, job)) return new(409, Code: "erasure_maintenance_ownership_changed");
        await keys.VerifyRequiredVersionsAsync(db, ct);
        job.Status = ErasureJobStatus.Running; job.ErrorCode = null; job.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return new(202, job);
    }

    public async Task<ErasureOutcome> DiscardUnreadableAsync(Guid projectId, Guid jobId, Guid actorId, IReadOnlyList<ErasureDiscardSelection> selected, CancellationToken ct)
    {
        if (selected.Count is < 1 or > 100 || selected.Any(i => (i.QueueSequence is null) == (i.DeadLetterId is null) || i.ContentHash?.Length != 64) ||
            selected.Select(i => (i.QueueSequence, i.DeadLetterId)).Distinct().Count() != selected.Count) return new(400, Code: "invalid_review_selection");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var state = await new QueueAdmissionService(db, new(), clock).AcquireGateAsync(projectId, ct);
        if (!await IsAdminAsync(projectId, actorId, ct)) return new(403);
        var job = await db.ErasureJobs.SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);
        if (job is null) return new(404);
        if (job.Status != ErasureJobStatus.NeedsReview) return new(409, Code: "job_does_not_need_review");
        if (!OwnsPause(state, job)) return new(409, Code: "erasure_maintenance_ownership_changed");
        var pending = await db.ErasureUnreadableItems.Where(i => i.ProjectId == projectId && i.JobId == jobId && i.DiscardedAt == null).ToListAsync(ct);
        var verified = new List<(ErasureUnreadableItem Report, Guid? Admission)>();
        foreach (var selection in selected)
        {
            var report = pending.SingleOrDefault(i => i.QueueSequence == selection.QueueSequence && i.DeadLetterId == selection.DeadLetterId);
            if (report is null || report.ContentHash != selection.ContentHash) return new(409, Code: "review_selection_changed");
            string? payload; Guid? admission;
            if (selection.QueueSequence is { } sequence)
            {
                var row = await db.QueuedEvents.AsNoTracking().SingleOrDefaultAsync(q => q.ProjectId == projectId && q.Seq == sequence, ct);
                payload = row?.PayloadJson; admission = row?.AdmissionId;
            }
            else
            {
                var row = await db.DeadLetterEvents.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == selection.DeadLetterId, ct);
                payload = row?.PayloadJson; admission = row?.AdmissionId;
            }
            if (payload is null || IdentitySuppressionService.ContentHash(payload) != report.ContentHash) return new(409, Code: "review_content_changed");
            verified.Add((report, admission));
        }
        var now = clock.GetUtcNow();
        foreach (var (report, admission) in verified)
        {
            await SuppressItemAsync(projectId, admission, "erasure_review_discard", ct);
            if (report.QueueSequence is { } sequence) job.RemovedQueueItems += await db.QueuedEvents.Where(q => q.ProjectId == projectId && q.Seq == sequence).ExecuteDeleteAsync(ct);
            else
            {
                await RetireLetterPointersAsync(projectId, report.DeadLetterId!.Value, ct);
                job.RemovedDeadLetters += await db.DeadLetterEvents.Where(d => d.ProjectId == projectId && d.Id == report.DeadLetterId).ExecuteDeleteAsync(ct);
            }
            report.DiscardedAt = now; report.DiscardedBy = actorId;
        }
        if (pending.All(i => i.DiscardedAt != null)) job.Status = ErasureJobStatus.Running;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return new(202, job);
    }

    private Task<bool> IsAdminAsync(Guid projectId, Guid actorId, CancellationToken ct) => db.ProjectMemberships.AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && m.Role == ProjectRole.Admin, ct);
    private static bool OwnsPause(ProjectIngestionState state, ErasureJob job) => state.Paused && state.ErasureJobId == job.Id &&
        state.MaintenanceGeneration == job.MaintenanceGeneration;
    private async Task SuppressItemAsync(Guid projectId, Guid? admissionId, string reason, CancellationToken ct)
    {
        if (admissionId is null) return;
        await new CaptureReceiptService(db, clock).TransitionAsync(projectId, admissionId, ProcessingItemState.Suppressed, null, null, ct);
        var item = db.CaptureProcessingItems.Local.Single(i => i.ProjectId == projectId && i.Id == admissionId);
        item.RetiredReason = reason;
    }
    private Task<int> RetireLetterPointersAsync(Guid projectId, Guid letterId, CancellationToken ct) => db.CaptureItemTransitions.Where(t => t.ProjectId == projectId && t.DeadLetterId == letterId)
        .ExecuteUpdateAsync(s => s.SetProperty(t => t.DeadLetterId, (Guid?)null), ct);
}
