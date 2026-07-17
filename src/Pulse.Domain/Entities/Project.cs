namespace Pulse.Domain.Entities;

/// <summary>
/// A workspace that owns events, persons and insights. Every project has a
/// write-only API key used to authenticate ingestion via <c>POST /capture</c>.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Write key presented by SDKs when capturing events.</summary>
    public required string ApiKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
