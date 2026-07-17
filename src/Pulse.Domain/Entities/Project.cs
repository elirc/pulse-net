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

    /// <summary>
    /// Read key (<c>rk_live_…</c>) granting query-only access to the project's
    /// insights endpoints — for embedding charts without a user account.
    /// </summary>
    public required string ReadKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
