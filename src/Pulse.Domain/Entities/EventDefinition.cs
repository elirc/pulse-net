namespace Pulse.Domain.Entities;

/// <summary>
/// Registry row for an event name seen in a project, auto-populated on
/// ingest. Powers autocomplete and the data-management UI.
/// </summary>
public class EventDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}
