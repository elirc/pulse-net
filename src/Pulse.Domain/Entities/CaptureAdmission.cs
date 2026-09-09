namespace Pulse.Domain.Entities;

/// <summary>A bounded client retry key; processing identity survives this row's expiry.</summary>
public class CaptureAdmissionKey
{
    public Guid ProjectId { get; set; }
    public Guid ClientEventId { get; set; }
    public Guid AdmissionId { get; set; }
    public required string PayloadHash { get; set; }
    public int FingerprintVersion { get; set; } = 1;
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public enum ProcessingItemState { Queued, Processed, DeadLettered, Suppressed }

public class CaptureProcessingItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public ProcessingItemState State { get; set; } = ProcessingItemState.Queued;
    public long ReplayGeneration { get; set; }
    public Guid? EventId { get; set; }
    public Guid? DeadLetterId { get; set; }
    public string? RetiredReason { get; set; }
}

public class CaptureReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Submitted positions can share one processing obligation after deduplication.</summary>
public class CaptureReceiptItem
{
    public Guid ReceiptId { get; set; }
    public int Ordinal { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AdmissionId { get; set; }
}

public class CaptureItemTransition
{
    public long Sequence { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AdmissionId { get; set; }
    public long ReplayGeneration { get; set; }
    public ProcessingItemState State { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? EventId { get; set; }
    public Guid? DeadLetterId { get; set; }
}
