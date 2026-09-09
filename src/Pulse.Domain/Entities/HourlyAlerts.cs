namespace Pulse.Domain.Entities;

/// <summary>A project-owned hourly event-count rule and its durable scheduler cursor.</summary>
public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string EventName { get; set; }
    public int Threshold { get; set; }
    public bool Enabled { get; set; }
    public bool IsDeleted { get; set; }
    public int Revision { get; set; } = 1;
    public DateTimeOffset NextWindowStart { get; set; }
    public DateTimeOffset? SkippedThrough { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Immutable evidence that one revision/window was evaluated once.</summary>
public sealed class AlertEvaluation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public Guid ProjectId { get; set; }
    public int RuleRevision { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public long ObservedCount { get; set; }
    public int Threshold { get; set; }
    public bool Triggered { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
}

/// <summary>Immutable in-app delivery facts copied from a triggering evaluation.</summary>
public sealed class ProjectNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid EvaluationId { get; set; }
    public Guid RuleId { get; set; }
    public int RuleRevision { get; set; }
    public required string RuleName { get; set; }
    public required string EventName { get; set; }
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public long ObservedCount { get; set; }
    public int Threshold { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A user's independent, idempotent read marker for one notification.</summary>
public sealed class NotificationRead
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ReadAt { get; set; }
}
