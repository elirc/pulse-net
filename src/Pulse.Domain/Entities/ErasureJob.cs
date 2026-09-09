namespace Pulse.Domain.Entities;

public enum ErasureJobStatus { Pending, Running, NeedsReview, Completed, Failed }
public enum ErasurePhase { Exports, SnapshotCopies, Events, Queue, DeadLetters, CohortLinks, Aliases, Person, Verify }

public class ErasureJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid PersonId { get; set; }
    public Guid RequestedBy { get; set; }
    public ErasureJobStatus Status { get; set; }
    public ErasurePhase Phase { get; set; }
    public long MaintenanceGeneration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ErrorCode { get; set; }
    public long QueueCursor { get; set; }
    public Guid? EventCursor { get; set; }
    public Guid? DeadLetterCursor { get; set; }
    public long RemovedEvents { get; set; }
    public long RemovedQueueItems { get; set; }
    public long RemovedDeadLetters { get; set; }
    public long RemovedAliases { get; set; }
    public long RemovedCohortLinks { get; set; }
    public long InvalidatedExports { get; set; }
}

/// <summary>Permanent project-scoped keyed identity fingerprints; never raw aliases.</summary>
public class IdentitySuppression
{
    public Guid ProjectId { get; set; }
    public int KeyVersion { get; set; }
    public required string Fingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Freezes the job's known identity set without retaining raw identities.</summary>
public class ErasureIdentity
{
    public Guid JobId { get; set; }
    public int KeyVersion { get; set; }
    public required string Fingerprint { get; set; }
}

public class ErasureUnreadableItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid ProjectId { get; set; }
    public long? QueueSequence { get; set; }
    public Guid? DeadLetterId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public Guid? DiscardedBy { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
}
