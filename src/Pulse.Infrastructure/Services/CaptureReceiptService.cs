using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record CaptureReceiptPosition(int Ordinal, Guid AdmissionId, string State, long ReplayGeneration, Guid? EventId, Guid? DeadLetterId, string? RetiredReason, DateTimeOffset ChangedAt);
public record CaptureReceiptStatus(Guid Id, Guid ProjectId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt,
    int Queued, int Processed, int DeadLettered, IReadOnlyList<CaptureReceiptPosition> Items, int Suppressed = 0);

public sealed class CaptureReceiptService(PulseDbContext db, TimeProvider clock)
{
    public async Task<CaptureReceiptStatus?> ReadAsync(Guid projectId, Guid receiptId, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var receipt = await db.CaptureReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ProjectId == projectId && r.Id == receiptId, ct);
        if (receipt is null) return null;
        var rows = await (from reference in db.CaptureReceiptItems.AsNoTracking()
                          join item in db.CaptureProcessingItems.AsNoTracking() on reference.AdmissionId equals item.Id
                          where reference.ProjectId == projectId && reference.ReceiptId == receiptId && item.ProjectId == projectId
                          orderby reference.Ordinal select new { reference.Ordinal, Item = item }).Take(1000).ToListAsync(ct);
        var items = rows.Select(r => new CaptureReceiptPosition(r.Ordinal, r.Item.Id, StateName(r.Item.State), r.Item.ReplayGeneration,
            r.Item.EventId, r.Item.DeadLetterId, r.Item.RetiredReason, r.Item.ChangedAt)).ToList();
        var queued = items.Count(i => i.State == "queued");
        DateTimeOffset? completed = items.Count > 0 && queued == 0 ? items.Max(i => i.ChangedAt) : null;
        var expires = completed?.AddDays(7);
        if (expires <= clock.GetUtcNow()) return null;
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(receipt.Id, projectId, receipt.CreatedAt, completed, expires, queued, items.Count(i => i.State == "processed"), items.Count(i => i.State == "deadLettered"), items, items.Count(i => i.State == "suppressed"));
    }

    public static string StateName(ProcessingItemState state) => state switch
    {
        ProcessingItemState.Queued => "queued", ProcessingItemState.Processed => "processed", ProcessingItemState.DeadLettered => "deadLettered", ProcessingItemState.Suppressed => "suppressed", _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public async Task TransitionAsync(Guid projectId, Guid? admissionId, ProcessingItemState state, Guid? eventId, Guid? letterId, CancellationToken ct)
    {
        if (admissionId is null) return; // Legacy rows have no invented processing history.
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Receipt transitions must join their processing transaction.");
        var item = await db.CaptureProcessingItems.SingleAsync(i => i.ProjectId == projectId && i.Id == admissionId, ct);
        if (state == ProcessingItemState.Queued)
        {
            if (item.State != ProcessingItemState.DeadLettered) throw new InvalidOperationException("Only failed items can be replayed.");
            item.ReplayGeneration++;
        }
        else if (state == ProcessingItemState.Suppressed)
        {
            if (item.State is not (ProcessingItemState.Queued or ProcessingItemState.DeadLettered)) throw new InvalidOperationException("Only queued or dead-lettered items can be suppressed.");
        }
        else if (item.State != ProcessingItemState.Queued) throw new InvalidOperationException("Only queued items can reach a processing outcome.");
        item.State = state; item.EventId = eventId; item.DeadLetterId = letterId; item.ChangedAt = clock.GetUtcNow(); item.RetiredReason = null;
        db.CaptureItemTransitions.Add(new CaptureItemTransition { ProjectId = projectId, AdmissionId = item.Id, State = state,
            ReplayGeneration = item.ReplayGeneration, ChangedAt = item.ChangedAt, EventId = eventId, DeadLetterId = letterId });
        // Keep the latest 19 persisted transitions plus the new staged transition.
        var old = await db.CaptureItemTransitions.Where(t => t.ProjectId == projectId && t.AdmissionId == item.Id)
            .OrderByDescending(t => t.Sequence).Skip(19).Select(t => t.Sequence).ToListAsync(ct);
        if (old.Count > 0) await db.CaptureItemTransitions.Where(t => old.Contains(t.Sequence)).ExecuteDeleteAsync(ct);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow(); var cutoff = now.AddDays(-7);
        var keys = await db.CaptureAdmissionKeys.Where(k => k.ExpiresAt <= now).OrderBy(k => k.ExpiresAt).Take(100).ToListAsync(ct);
        db.CaptureAdmissionKeys.RemoveRange(keys);
        // Expiry follows the final terminal item, not the time the receipt was created.
        var expired = await (from reference in db.CaptureReceiptItems
                             join item in db.CaptureProcessingItems on reference.AdmissionId equals item.Id
                             group item by reference.ReceiptId into receipt
                             where !receipt.Any(i => i.State == ProcessingItemState.Queued) && receipt.Max(i => i.ChangedAt) <= cutoff
                             select receipt.Key).Take(100).ToListAsync(ct);
        if (expired.Count > 0)
        {
            await db.CaptureReceiptItems.Where(r => expired.Contains(r.ReceiptId)).ExecuteDeleteAsync(ct);
            await db.CaptureReceipts.Where(r => expired.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        var retired = await db.CaptureProcessingItems.Where(i => i.State != ProcessingItemState.Queued && i.ChangedAt <= cutoff &&
            !db.CaptureReceiptItems.Any(r => r.AdmissionId == i.Id) && !db.CaptureAdmissionKeys.Any(k => k.AdmissionId == i.Id) &&
            !db.QueuedEvents.Any(q => q.AdmissionId == i.Id) && !db.DeadLetterEvents.Any(d => d.AdmissionId == i.Id))
            .OrderBy(i => i.ChangedAt).Select(i => i.Id).Take(100).ToListAsync(ct);
        if (retired.Count > 0)
        {
            await db.CaptureItemTransitions.Where(t => retired.Contains(t.AdmissionId)).ExecuteDeleteAsync(ct);
            await db.CaptureProcessingItems.Where(i => retired.Contains(i.Id)).ExecuteDeleteAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
