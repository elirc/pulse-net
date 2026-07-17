namespace Pulse.Domain.Entities;

/// <summary>
/// One event awaiting ingestion. <c>POST /capture</c> appends rows here and
/// returns 202; the background processor drains them in enqueue order (the
/// auto-increment <see cref="Seq"/> preserves $identify ordering) and either
/// persists them or moves them to <see cref="DeadLetterEvent"/>.
/// </summary>
public class QueuedEvent
{
    /// <summary>Auto-increment primary key; processing order.</summary>
    public long Seq { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The serialized incoming event (name, distinct id, timestamp, properties).</summary>
    public required string PayloadJson { get; set; }

    /// <summary>Transient-failure retries so far.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;
}
