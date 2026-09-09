namespace Pulse.Domain.Entities;

public class ProjectRetentionPolicy
{
    public Guid ProjectId { get; set; }
    public bool Enabled { get; set; }
    public int Days { get; set; } = 365;
    public long Revision { get; set; } = 1;
}

public enum RetentionRunStatus { Running, Completed, Superseded }

public class RetentionRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public long PolicyRevision { get; set; }
    public DateTimeOffset Cutoff { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public RetentionRunStatus Status { get; set; }
    public long RemovedCount { get; set; }
    public int Batches { get; set; }
    public DateTimeOffset? LastBatchAt { get; set; }
}
