namespace Pulse.Domain.Entities;

public enum ExportJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// An asynchronous export. Small exports can use the synchronous endpoints;
/// large ones create a job, poll its status, then download the result.
/// The finished document is stored inline — plenty for a SQLite-backed
/// deployment; swap for object storage when results outgrow the database.
/// </summary>
public class ExportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    /// <summary>What to export: <c>events</c>, <c>persons</c> or <c>insight</c>.</summary>
    public required string Type { get; set; }

    /// <summary><c>csv</c> or <c>json</c>.</summary>
    public required string Format { get; set; }

    /// <summary>Type-specific parameters (event/from/to/filters/insightId) as JSON.</summary>
    public string ParamsJson { get; set; } = "{}";

    public ExportJobStatus Status { get; set; } = ExportJobStatus.Pending;

    /// <summary>The rendered export document, set when completed.</summary>
    public string? ResultContent { get; set; }

    public string? ContentType { get; set; }

    public int RowCount { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
