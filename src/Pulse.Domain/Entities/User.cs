namespace Pulse.Domain.Entities;

/// <summary>
/// An operator of the management API (dashboards, insights, project admin).
/// Distinct from <see cref="Person"/>, which models the *tracked* end users.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Login email, unique per deployment, stored lowercase.</summary>
    public required string Email { get; set; }

    public required string Name { get; set; }

    /// <summary>PBKDF2 hash in <c>iterations.salt.hash</c> format.</summary>
    public required string PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
