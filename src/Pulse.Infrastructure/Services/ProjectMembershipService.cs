using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record MembershipMutation(int Status, ProjectMembership? Membership = null, bool Changed = false);

/// <summary>All membership mutations serialize on the project before rechecking authority and the last admin.</summary>
public sealed class ProjectMembershipService(PulseDbContext db, TimeProvider clock)
{
    private async Task<int> AcquireAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        var found = await db.Projects.Where(p => p.Id == projectId)
            .ExecuteUpdateAsync(update => update.SetProperty(p => p.Name, p => p.Name), ct);
        if (found == 0) return 404;
        var role = await db.ProjectMemberships.Where(m => m.ProjectId == projectId && m.UserId == actorId)
            .Select(m => (ProjectRole?)m.Role).SingleOrDefaultAsync(ct);
        return role is null ? 404 : role == ProjectRole.Admin ? 200 : 403;
    }

    public async Task<MembershipMutation> InviteAsync(Guid projectId, Guid actorId, Guid userId, CancellationToken ct, Guid? personalKeyId = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var permission = await AcquireAsync(projectId, actorId, ct);
        if (permission != 200) return new(permission);
        var membership = await db.ProjectMemberships.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        var changed = membership is null;
        if (membership is null)
        {
            membership = new ProjectMembership { ProjectId = projectId, UserId = userId, Role = ProjectRole.Viewer, CreatedAt = clock.GetUtcNow() };
            db.ProjectMemberships.Add(membership);
            ManagementAuditWriter.Member(db, projectId, membership.Id, new(actorId, personalKeyId), null, membership.Role, membership.CreatedAt);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(201, membership, changed);
    }

    public async Task<MembershipMutation> ChangeAsync(Guid projectId, Guid actorId, Guid userId, ProjectRole? newRole, CancellationToken ct, Guid? personalKeyId = null)
    {
        if (newRole is { } candidate && !Enum.IsDefined(candidate)) return new(400);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var permission = await AcquireAsync(projectId, actorId, ct);
        if (permission != 200) return new(permission);
        var membership = await db.ProjectMemberships.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (membership is null) return new(404);
        if (membership.Role == ProjectRole.Admin && newRole != ProjectRole.Admin &&
            await db.ProjectMemberships.CountAsync(m => m.ProjectId == projectId && m.Role == ProjectRole.Admin, ct) <= 1)
            return new(409);
        var changed = newRole is null || membership.Role != newRole.Value;
        var previousRole = membership.Role;
        if (newRole is { } role) membership.Role = role;
        else db.ProjectMemberships.Remove(membership);
        if (changed) ManagementAuditWriter.Member(db, projectId, membership.Id, new(actorId, personalKeyId), previousRole, newRole, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(newRole is null ? 204 : 200, membership, changed);
    }
}
