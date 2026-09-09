using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record CaptureAdmissionOutcome(int Status, int Queued = 0, int Deduplicated = 0, Guid? ReceiptId = null, string? Code = null);
public record QueueLimits(int? MaxPending, int Pending, bool Paused);

/// <summary>Capture, operational replay, and limit edits serialize on one durable project gate.</summary>
public sealed class QueueAdmissionService(PulseDbContext db, IngestionSignal signal, TimeProvider clock, SuppressionKeyRing? suppressionKeys = null, ILogger<QueueAdmissionService>? logger = null)
{
    public async Task<ProjectIngestionState> AcquireGateAsync(Guid projectId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Admission gate requires a transaction.");
        // Direct legacy/test seed paths may not have provisioned settings yet.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProjectIngestionStates (ProjectId, GateVersion, MaxPending, Paused, MaintenanceGeneration, ErasureJobId) VALUES ({projectId}, 0, NULL, 0, 0, NULL) ON CONFLICT(ProjectId) DO NOTHING", ct);
        await db.ProjectIngestionStates.Where(s => s.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.GateVersion, x => x.GateVersion + 1), ct);
        return await db.ProjectIngestionStates.AsNoTracking().SingleAsync(s => s.ProjectId == projectId, ct);
    }

    public async Task<bool> FitsAsync(ProjectIngestionState settings, int newRows, CancellationToken ct) => newRows == 0 || settings.MaxPending is null ||
        await db.QueuedEvents.LongCountAsync(q => q.ProjectId == settings.ProjectId, ct) + newRows <= settings.MaxPending;

    public async Task<QueueLimits> ReadLimitsAsync(Guid projectId, CancellationToken ct)
    {
        var settings = await db.ProjectIngestionStates.AsNoTracking().SingleOrDefaultAsync(s => s.ProjectId == projectId, ct);
        return new(settings?.MaxPending, await db.QueuedEvents.CountAsync(q => q.ProjectId == projectId, ct), settings?.Paused ?? false);
    }

    public async Task<QueueLimits> SetLimitAsync(Guid projectId, int? maxPending, CancellationToken ct)
    {
        if (maxPending is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxPending));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcquireGateAsync(projectId, ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        await db.ProjectIngestionStates.Where(s => s.ProjectId == projectId).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxPending, maxPending), ct);
        var result = await ReadLimitsAsync(projectId, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<CaptureAdmissionOutcome> AdmitAsync(Guid projectId, IReadOnlyList<IncomingEvent> events, bool receiptRequested, CancellationToken ct)
    {
        using var trace = IngestionTrace.Producer("ingestion.admit");
        if (events.Count is < 1 or > 1000) return new(400, Code: "invalid_batch_size");
        var fingerprints = new string?[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (item.ClientEventId is not null && (item.ClientEventId == Guid.Empty ||
                !CaptureFingerprint.TryCreate(item.Name, item.DistinctId, item.Timestamp, item.PropertiesJson, out fingerprints[i]!)))
                return new(400, Code: "invalid_idempotent_payload");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var settings = await AcquireGateAsync(projectId, ct);
        if (settings.Paused) return new(503, Code: "project_maintenance");
        if (await new IdentitySuppressionService(db, suppressionKeys ?? SuppressionKeyRing.Unconfigured).MatchesAnyAsync(projectId, events, ct))
            return new(422, Code: "identity_suppressed");
        var now = clock.GetUtcNow();
        var ids = events.Where(e => e.ClientEventId is not null).Select(e => e.ClientEventId!.Value).Distinct().ToArray();
        var stored = await db.CaptureAdmissionKeys.AsNoTracking().Where(k => k.ProjectId == projectId && ids.Contains(k.ClientEventId)).ToDictionaryAsync(k => k.ClientEventId, ct);
        var currentIds = stored.Values.Where(k => k.ExpiresAt > now).Select(k => k.AdmissionId).ToArray();
        var knownItems = receiptRequested ? await db.CaptureProcessingItems.Where(i => i.ProjectId == projectId && currentIds.Contains(i.Id)).Select(i => i.Id).ToHashSetAsync(ct) : [];
        var submitted = new Dictionary<Guid, (string Hash, Guid Admission)>();
        var references = new List<Guid>(events.Count);
        var admitted = new List<(IncomingEvent Event, Guid Admission)>();
        var newKeys = new List<CaptureAdmissionKey>();
        var expired = new List<Guid>();
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index];
            Guid admissionId;
            if (item.ClientEventId is { } clientId)
            {
                var hash = fingerprints[index]!;
                if (submitted.TryGetValue(clientId, out var prior))
                {
                    if (prior.Hash != hash) return new(409, Code: "event_id_payload_conflict");
                    references.Add(prior.Admission); continue;
                }
                if (stored.TryGetValue(clientId, out var existing) && existing.ExpiresAt > now)
                {
                    if (existing.FingerprintVersion != CaptureFingerprint.Version || existing.PayloadHash != hash) return new(409, Code: "event_id_payload_conflict");
                    if (receiptRequested && !knownItems.Contains(existing.AdmissionId)) return new(409, Code: "legacy_processing_metadata_unavailable");
                    submitted[clientId] = (hash, existing.AdmissionId); references.Add(existing.AdmissionId); continue;
                }
                if (existing is not null) expired.Add(clientId);
                admissionId = Guid.NewGuid();
                newKeys.Add(new CaptureAdmissionKey { ProjectId = projectId, ClientEventId = clientId, AdmissionId = admissionId,
                    PayloadHash = hash, AcceptedAt = now, ExpiresAt = now.AddDays(7) });
                submitted[clientId] = (hash, admissionId);
            }
            else admissionId = Guid.NewGuid();
            references.Add(admissionId); admitted.Add((item, admissionId));
        }
        if (!await FitsAsync(settings, admitted.Count, ct)) return new(429, Code: "queue_capacity_exceeded");
        // All conflict/capacity checks completed before any queue/key/receipt insertion.
        if (expired.Count > 0)
        {
            await db.CaptureAdmissionKeys.Where(k => k.ProjectId == projectId && expired.Contains(k.ClientEventId)).ExecuteDeleteAsync(ct);
            foreach (var tracked in db.CaptureAdmissionKeys.Local.Where(k => k.ProjectId == projectId && expired.Contains(k.ClientEventId)).ToList())
                db.Entry(tracked).State = EntityState.Detached;
        }
        db.CaptureAdmissionKeys.AddRange(newKeys);
        foreach (var (item, id) in admitted)
        {
            db.CaptureProcessingItems.Add(new CaptureProcessingItem { Id = id, ProjectId = projectId, AcceptedAt = now, ChangedAt = now });
            db.QueuedEvents.Add(new QueuedEvent { ProjectId = projectId, AdmissionId = id, PayloadJson = JsonSerializer.Serialize(item), EnqueuedAt = now,
                TraceParent = IngestionTrace.Format(trace.Context) });
        }
        CaptureReceipt? receipt = null;
        if (receiptRequested)
        {
            receipt = new CaptureReceipt { ProjectId = projectId, CreatedAt = now };
            db.CaptureReceipts.Add(receipt);
            db.CaptureReceiptItems.AddRange(references.Select((id, ordinal) => new CaptureReceiptItem { ProjectId = projectId, ReceiptId = receipt.Id, Ordinal = ordinal, AdmissionId = id }));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        IngestionTrace.Log(logger, trace, "admitted", duplicates: events.Count - admitted.Count);
        if (admitted.Count > 0) signal.Ring();
        return new(202, admitted.Count, events.Count - admitted.Count, receipt?.Id);
    }
}
