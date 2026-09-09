using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record ProjectIngestionMetrics(
    int Pending, int DeadLetters, DateTimeOffset? OldestEnqueuedAt, double? OldestPendingAgeSeconds,
    int Delayed = 0, DateTimeOffset? NextAttemptAt = null);

public enum DeadLetterReplayOutcome { Queued, NotFound, InvalidPayload, CapacityExceeded, Paused, Suppressed }
public record ReplayBatchItem(Guid LetterId, string Outcome);
public record ReplayBatchResult(IReadOnlyList<ReplayBatchItem> Results, int Queued, int Invalid, int NotFound, int CapacityExceeded = 0, int Paused = 0, int Suppressed = 0);

/// <summary>Project-scoped operational queries and atomic recovery of one failed event.</summary>
public class IngestionOperationsService(PulseDbContext db, IngestionSignal signal, TimeProvider clock, SuppressionKeyRing? suppressionKeys = null, ILogger<IngestionOperationsService>? logger = null)
{
    public Task<ProjectIngestionMetrics> GetMetricsAsync(Guid projectId, CancellationToken ct) =>
        GetMetricsAsync(projectId, clock.GetUtcNow(), ct);

    public async Task<ProjectIngestionMetrics> GetMetricsAsync(Guid projectId, DateTimeOffset observedAt, CancellationToken ct)
    {
        var queue = await db.QueuedEvents.Where(q => q.ProjectId == projectId)
            .GroupBy(q => q.ProjectId)
            .Select(g => new { Pending = g.Count(), Oldest = g.Min(q => q.EnqueuedAt), Delayed = g.Count(q => q.NextAttemptAt > observedAt),
                NextAttemptAt = g.Where(q => q.NextAttemptAt > observedAt).Min(q => q.NextAttemptAt) })
            .SingleOrDefaultAsync(ct);
        var deadLetters = await db.DeadLetterEvents.CountAsync(d => d.ProjectId == projectId, ct);
        return new ProjectIngestionMetrics(queue?.Pending ?? 0, deadLetters, queue?.Oldest,
            queue is null ? null : Math.Max(0, (observedAt - queue.Oldest).TotalSeconds), queue?.Delayed ?? 0, queue?.NextAttemptAt);
    }

    public async Task<DeadLetterReplayOutcome> ReplayAsync(Guid projectId, Guid letterId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var admission = new QueueAdmissionService(db, signal, clock);
        var settings = await admission.AcquireGateAsync(projectId, ct);
        if (settings.Paused) return DeadLetterReplayOutcome.Paused;
        var letter = await db.DeadLetterEvents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ProjectId == projectId && d.Id == letterId, ct);
        if (letter is null)
        {
            return DeadLetterReplayOutcome.NotFound;
        }
        var original = IngestionTrace.Normalize(letter.OriginalTraceParent) ?? IngestionTrace.Normalize(letter.TraceParent);
        using var trace = IngestionTrace.Producer("ingestion.replay", original);

        if (!ReplayEnvelopeValidator.Validate(letter.PayloadJson).Replayable)
        {
            return DeadLetterReplayOutcome.InvalidPayload;
        }
        var incoming = JsonSerializer.Deserialize<IncomingEvent>(letter.PayloadJson)!;
        if (await new IdentitySuppressionService(db, suppressionKeys ?? SuppressionKeyRing.Unconfigured).MatchesAnyAsync(projectId, [incoming], ct)) return DeadLetterReplayOutcome.Suppressed;
        if (!await admission.FitsAsync(settings, 1, ct)) return DeadLetterReplayOutcome.CapacityExceeded;

        // A transaction prevents losing the letter if enqueue fails. The
        // project predicate also prevents accidentally replaying another tenant's data.
        var removed = await db.DeadLetterEvents
            .Where(d => d.ProjectId == projectId && d.Id == letterId).ExecuteDeleteAsync(ct);
        if (removed != 1)
        {
            return DeadLetterReplayOutcome.NotFound;
        }

        db.QueuedEvents.Add(new QueuedEvent
        {
            ProjectId = projectId,
            AdmissionId = letter.AdmissionId,
            TraceParent = IngestionTrace.Format(trace.Context),
            OriginalTraceParent = original,
            PayloadJson = letter.PayloadJson,
            Attempts = 0,
            EnqueuedAt = clock.GetUtcNow(),
        });
        await new CaptureReceiptService(db, clock).TransitionAsync(projectId, letter.AdmissionId, ProcessingItemState.Queued, null, null, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        IngestionTrace.Log(logger, trace, "replayed");
        signal.Ring(); // The durable write must be visible before waking the worker.
        return DeadLetterReplayOutcome.Queued;
    }

    public async Task<ReplayBatchResult> ReplayBatchAsync(Guid projectId, IReadOnlyList<Guid> letterIds, CancellationToken ct)
    {
        var results = new List<ReplayBatchItem>();
        foreach (var id in letterIds)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await ReplayAsync(projectId, id, ct);
            results.Add(new(id, outcome switch
            {
                DeadLetterReplayOutcome.Queued => "queued", DeadLetterReplayOutcome.InvalidPayload => "invalidPayload",
                DeadLetterReplayOutcome.CapacityExceeded => "capacityExceeded", DeadLetterReplayOutcome.Paused => "paused", DeadLetterReplayOutcome.Suppressed => "suppressed", _ => "notFound",
            }));
        }
        return new(results, results.Count(r => r.Outcome == "queued"), results.Count(r => r.Outcome == "invalidPayload"), results.Count(r => r.Outcome == "notFound"),
            results.Count(r => r.Outcome == "capacityExceeded"), results.Count(r => r.Outcome == "paused"), results.Count(r => r.Outcome == "suppressed"));
    }
}
