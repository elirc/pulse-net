namespace Pulse.Domain.Entities;

/// <summary>Kind of saved analytics query.</summary>
public enum InsightType
{
    Trend,
    Funnel,
    Retention,
}

/// <summary>
/// A saved analytics query (trend, funnel or retention) with its parameters
/// stored as JSON so each query shape can evolve independently.
/// </summary>
public class Insight
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public required string Name { get; set; }

    public InsightType Type { get; set; }

    /// <summary>Query parameters (events, steps, window, ...) as JSON.</summary>
    public string ConfigJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
