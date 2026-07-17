namespace Pulse.Domain.Entities;

/// <summary>
/// A long-lived credential for scripting the management API as a user
/// (<c>Authorization: Bearer pk_user_…</c>). Only the SHA-256 hash is stored;
/// the plaintext key is shown once at creation.
/// </summary>
public class PersonalApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Human label, e.g. "CI deploy script".</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 of the plaintext key, lowercase hex.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Last four characters of the key, kept for display.</summary>
    public required string KeySuffix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
