namespace Pulse.Domain.Entities;

/// <summary>
/// Registry row for an event property key seen in a project, auto-populated
/// on ingest. System (<c>$…</c>) keys are excluded. The type reflects the
/// first JSON kind observed for the key.
/// </summary>
public class PropertyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    /// <summary>JSON kind: string, number, boolean, object or array.</summary>
    public required string PropertyType { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
