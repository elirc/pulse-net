namespace Pulse.Domain.Entities;

/// <summary>
/// A poison event the processor gave up on: malformed payload, failed
/// validation, or exhausted transient retries. Kept for inspection instead
/// of blocking the queue.
/// </summary>
public class DeadLetterEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string PayloadJson { get; set; }

    /// <summary>Why the event was dead-lettered.</summary>
    public required string Error { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset FailedAt { get; set; } = DateTimeOffset.UtcNow;
}
