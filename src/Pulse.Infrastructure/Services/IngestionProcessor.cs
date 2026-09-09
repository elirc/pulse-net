using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>Bounded fair project batches with durable retries and a lease fence on every mutation.</summary>
public class IngestionProcessor
{
    public const int MaxAttempts = IngestionRetryPolicy.MaxAttempts;
    public const int BatchSize = 200;
    public const int ProjectBatchSize = 50;
    private readonly PulseDbContext db;
    private readonly CaptureService capture;
    private readonly IngestionCounters counters;
    private readonly TimeProvider clock;
    private readonly IngestionWorkerIdentity identity;
    private readonly IngestionLeaseService leases;
    private readonly ILogger<IngestionProcessor>? logger;

    public IngestionProcessor(PulseDbContext db, CaptureService capture, IngestionCounters counters,
        TimeProvider? clock = null, IngestionWorkerIdentity? identity = null, ILogger<IngestionProcessor>? logger = null)
    {
        this.db = db; this.capture = capture; this.counters = counters;
        this.clock = clock ?? TimeProvider.System; this.identity = identity ?? new();
        leases = new(db, this.clock);
        this.logger = logger;
    }

    public async Task<(int Processed, int DeadLettered)> ProcessPendingAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var dueProjects = db.QueuedEvents.AsNoTracking().Where(q => (q.NextAttemptAt == null || q.NextAttemptAt <= now) &&
            !db.ProjectIngestionStates.Any(s => s.ProjectId == q.ProjectId && s.Paused))
            .GroupBy(q => q.ProjectId).Select(g => new { ProjectId = g.Key, First = g.Min(q => q.Seq) });
        var projects = await (from project in dueProjects
                              join lease in db.ProjectIngestionLeases on project.ProjectId equals lease.ProjectId into leaseRows
                              from lease in leaseRows.DefaultIfEmpty()
                              orderby lease == null ? DateTimeOffset.MinValue : lease.LastServedAt, project.First
                              select project.ProjectId).Take(20).ToListAsync(ct);
        var processed = 0; var deadLettered = 0; var attempted = 0;
        foreach (var projectId in projects)
        {
            var lease = await leases.ClaimAsync(projectId, identity.Owner, ct);
            if (lease is null) continue;
            try
            {
                var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, ct);
                now = clock.GetUtcNow();
                var rows = await db.QueuedEvents.AsNoTracking().Where(q => q.ProjectId == projectId && (q.NextAttemptAt == null || q.NextAttemptAt <= now))
                    .OrderBy(q => q.Seq).Take(Math.Min(ProjectBatchSize, BatchSize - attempted)).ToListAsync(ct);
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!await leases.RenewAsync(lease, ct)) break;
                    attempted++;
                    var result = await ProcessLeasedRowAsync(row, project, lease, ct);
                    if (result == "processed") { processed++; counters.AddProcessed(1); }
                    if (result == "deadLettered") { deadLettered++; counters.AddDeadLettered(1); }
                    db.ChangeTracker.Clear();
                }
            }
            catch (Exception error) when (error is LostIngestionLeaseException or IngestionPausedException or ProjectMaintenanceException) { db.ChangeTracker.Clear(); }
            finally { if (!ct.IsCancellationRequested) await leases.ReleaseAsync(lease, ct); }
            if (attempted >= BatchSize) break;
        }
        return (processed, deadLettered);
    }

    // Public for deterministic ownership tests: callers still cannot bypass the transaction fence.
    public async Task<string> ProcessLeasedRowAsync(QueuedEvent row, Project? project, IngestionLeaseToken lease, CancellationToken ct)
    {
        using var trace = IngestionTrace.Consumer(row.TraceParent, row.OriginalTraceParent);
        try
        {
            var outcome = await ProcessLeasedRowCoreAsync(row, project, lease, ct);
            IngestionTrace.Log(logger, trace, outcome, row.Seq, row.Attempts + 1);
            return outcome;
        }
        catch (OperationCanceledException) { IngestionTrace.Log(logger, trace, "cancelled", row.Seq, row.Attempts + 1); throw; }
        catch { IngestionTrace.Log(logger, trace, "failed", row.Seq, row.Attempts + 1); throw; }
    }

    private async Task<string> ProcessLeasedRowCoreAsync(QueuedEvent row, Project? project, IngestionLeaseToken lease, CancellationToken ct)
    {
        if (row.ProjectId != lease.ProjectId) throw new LostIngestionLeaseException();
        if (row.NextAttemptAt > clock.GetUtcNow()) return "delayed";
        if (project is null)
        {
            await MoveToDeadLetterAsync(row, lease, row.Attempts + 1, "project_missing", "Project no longer exists.", ct);
            return "deadLettered";
        }
        var validation = ReplayEnvelopeValidator.Validate(row.PayloadJson);
        if (!validation.Replayable)
        {
            await MoveToDeadLetterAsync(row, lease, row.Attempts + 1, "invalid_envelope", "Payload failed validation.", ct);
            return "deadLettered";
        }
        var incoming = JsonSerializer.Deserialize<IncomingEvent>(row.PayloadJson)!;
        try
        {
            var result = await capture.IngestQueuedAsync(project, incoming, row.Seq, lease, ct);
            return result.Ingested == 0 ? "suppressed" : "processed";
        }
        catch (Exception error) when (IngestionRetryPolicy.IsTransient(error))
        {
            db.ChangeTracker.Clear();
            var retry = IngestionRetryPolicy.AfterTransientFailure(row.Attempts);
            if (retry.DeadLetter)
            {
                await MoveToDeadLetterAsync(row, lease, retry.FailedAttempts, retry.Code, "SQLite contention exhausted the processing retry budget.", ct);
                return "deadLettered";
            }
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await leases.FenceAsync(lease, ct);
            var next = clock.GetUtcNow().Add(retry.Delay!.Value);
            var changed = await db.QueuedEvents.Where(q => q.ProjectId == row.ProjectId && q.Seq == row.Seq && q.Attempts == row.Attempts)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Attempts, retry.FailedAttempts).SetProperty(q => q.NextAttemptAt, next)
                    .SetProperty(q => q.LastErrorCode, retry.Code), ct);
            if (changed != 1) throw new LostIngestionLeaseException();
            await transaction.CommitAsync(ct);
            return "delayed";
        }
        catch
        {
            // Cancellation and unclassified storage faults consume no attempt.
            // A new cycle/context can investigate/retry; do not call arbitrary exceptions poison.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task MoveToDeadLetterAsync(QueuedEvent row, IngestionLeaseToken lease, int failedAttempts, string code, string message, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await leases.FenceAsync(lease, ct);
        var removed = await db.QueuedEvents.Where(q => q.ProjectId == row.ProjectId && q.Seq == row.Seq && q.Attempts == row.Attempts).ExecuteDeleteAsync(ct);
        if (removed != 1) throw new LostIngestionLeaseException();
        var letter = new DeadLetterEvent { ProjectId = row.ProjectId, AdmissionId = row.AdmissionId, PayloadJson = row.PayloadJson,
            TraceParent = IngestionTrace.Normalize(row.TraceParent), OriginalTraceParent = IngestionTrace.Normalize(row.OriginalTraceParent),
            Attempts = failedAttempts, FailedAt = clock.GetUtcNow(), Error = code + ": " + message };
        db.DeadLetterEvents.Add(letter);
        await new CaptureReceiptService(db, clock).TransitionAsync(row.ProjectId, row.AdmissionId, ProcessingItemState.DeadLettered, null, letter.Id, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
