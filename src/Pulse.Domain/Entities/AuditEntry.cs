namespace Pulse.Domain.Entities;

/// <summary>Application audit evidence committed in the covered mutation's transaction.</summary>
public class AuditEntry
{
    public long Sequence { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? PersonalKeyId { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public required string SummaryJson { get; set; }
}
