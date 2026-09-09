namespace Pulse.Domain.Entities;

public enum FlagScheduleStatus { Pending, Applied, Cancelled, Conflict, Blocked }

/// <summary>One latest schedule row per flag, replaced only after it becomes terminal.</summary>
public class FlagRolloutSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid FlagId { get; set; }
    public Guid CreatorUserId { get; set; }
    public Guid? PersonalKeyId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExecuteAt { get; set; }
    public double RolloutPercentage { get; set; }
    public long ExpectedRevision { get; set; }
    public FlagScheduleStatus Status { get; set; } = FlagScheduleStatus.Pending;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Reason { get; set; }
    public long? AppliedRevision { get; set; }
}
