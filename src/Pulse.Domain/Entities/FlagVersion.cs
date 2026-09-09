namespace Pulse.Domain.Entities;

/// <summary>An immutable configuration snapshot; restoration creates another revision.</summary>
public class FlagVersion
{
    public Guid ProjectId { get; set; }
    public Guid FlagId { get; set; }
    public long Revision { get; set; }
    public required string ConfigJson { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? PersonalKeyId { get; set; }
    public string Origin { get; set; } = "updated";
    public long? RestoredFromRevision { get; set; }
}
