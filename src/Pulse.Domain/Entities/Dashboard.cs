namespace Pulse.Domain.Entities;

/// <summary>A named collection of insight tiles arranged on a grid.</summary>
public class Dashboard
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
