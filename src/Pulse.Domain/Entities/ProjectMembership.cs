namespace Pulse.Domain.Entities;

/// <summary>
/// Grants a user access to a project. Management-API authorization is
/// membership-based: no membership, no visibility (requests 404 rather than
/// 403 so project ids don't leak).
/// </summary>
public class ProjectMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
