namespace Pulse.Domain.Entities;

/// <summary>
/// A single captured product event ("pageview", "signup", ...). Properties are
/// stored as a JSON document, PostHog-style, so arbitrary payloads survive.
/// </summary>
public class AnalyticsEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    /// <summary>Event name, e.g. <c>pageview</c> or <c>$identify</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The distinct id the SDK sent (anonymous or identified).</summary>
    public required string DistinctId { get; set; }

    /// <summary>The person this event resolved to at ingestion time.</summary>
    public Guid? PersonId { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Arbitrary event properties serialized as a JSON object.</summary>
    public string PropertiesJson { get; set; } = "{}";
}
