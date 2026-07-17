namespace Pulse.Domain.Entities;

/// <summary>
/// A dated note ("v2.3 released", "TV campaign started") surfaced on trend
/// charts whose range covers the date.
/// </summary>
public class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public DateOnly Date { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
