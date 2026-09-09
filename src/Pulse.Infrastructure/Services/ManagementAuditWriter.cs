using System.Text;
using System.Text.Json;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record AuditActor(Guid UserId, Guid? PersonalKeyId = null);

/// <summary>Stages allowlisted evidence on the caller's context; never commits independently.</summary>
public static class ManagementAuditWriter
{
    public static readonly string[] Actions = ["member.added", "member.role_changed", "member.removed", "flag.created", "flag.updated", "flag.deleted"];

    public static void Member(PulseDbContext db, Guid projectId, Guid membershipId, AuditActor actor,
        ProjectRole? previousRole, ProjectRole? role, DateTimeOffset now)
    {
        var action = previousRole is null ? "member.added" : role is null ? "member.removed" : "member.role_changed";
        Stage(db, projectId, membershipId, "membership", action, actor, now,
            new { previousRole = previousRole?.ToString().ToLowerInvariant(), role = role?.ToString().ToLowerInvariant() });
    }

    public static void Flag(PulseDbContext db, FeatureFlag flag, AuditActor actor, string action, DateTimeOffset now,
        long? previousRevision = null, long? restoredFrom = null, Guid? scheduleId = null)
    {
        if (action is not ("flag.created" or "flag.updated" or "flag.deleted")) throw new ArgumentException("Unsupported flag audit action.", nameof(action));
        // Names, keys, targeting, and variants are deliberately absent. Summary
        // fields describe the mutation without retaining arbitrary user text.
        Stage(db, flag.ProjectId, flag.Id, "featureFlag", action, actor, now,
            new { revision = flag.Revision, previousRevision, restoredFromRevision = restoredFrom, scheduleId });
    }

    private static void Stage(PulseDbContext db, Guid projectId, Guid resourceId, string resourceType,
        string action, AuditActor actor, DateTimeOffset now, object summary)
    {
        if (actor.UserId == Guid.Empty) throw new ArgumentException("An authenticated actor is required.", nameof(actor));
        var json = JsonSerializer.Serialize(summary, JsonSerializerOptions.Web);
        if (Encoding.UTF8.GetByteCount(json) > 2048) throw new InvalidOperationException("Audit summary exceeds its bounded contract.");
        db.AuditEntries.Add(new AuditEntry { ProjectId = projectId, ResourceId = resourceId, ResourceType = resourceType, Action = action,
            ActorUserId = actor.UserId, PersonalKeyId = actor.PersonalKeyId, RecordedAt = now, SummaryJson = json });
    }
}
