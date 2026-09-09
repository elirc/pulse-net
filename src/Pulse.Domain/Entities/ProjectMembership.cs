namespace Pulse.Domain.Entities;

public enum ProjectRole { Viewer, Editor, Admin }

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

    public ProjectRole Role { get; set; } = ProjectRole.Viewer;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
