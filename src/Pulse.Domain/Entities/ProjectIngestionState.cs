namespace Pulse.Domain.Entities;

/// <summary>Durable admission settings and a shared serialization point for capture/replay/limit edits.</summary>
public class ProjectIngestionState
{
    public Guid ProjectId { get; set; }
    public long GateVersion { get; set; }
    public int? MaxPending { get; set; }
    public bool Paused { get; set; }
    public long MaintenanceGeneration { get; set; }
    public Guid? ErasureJobId { get; set; }
}

public class ProjectIngestionLease
{
    public Guid ProjectId { get; set; }
    public Guid? Owner { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastServedAt { get; set; }
}
