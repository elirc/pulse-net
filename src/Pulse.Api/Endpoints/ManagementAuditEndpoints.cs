using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class ManagementAuditEndpoints
{
    public static IEndpointRouteBuilder MapManagementAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/audit", async (Guid projectId, int? limit, string? cursor, string? action,
            HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (limit is < 1 or > 200) return InputRules.Problem("limit", "Use a limit from 1 to 200.");
            if (action is not null && !ManagementAuditWriter.Actions.Contains(action, StringComparer.Ordinal)) return InputRules.Problem("action", "Use a supported management audit action.");
            if (!AuditCursor.TryDecode(cursor, projectId, action, out var position)) return InputRules.Problem("cursor", "The cursor is invalid or belongs to another project or filter.");
            var query = db.AuditEntries.AsNoTracking().Where(e => e.ProjectId == projectId && (action == null || e.Action == action));
            if (position is not null) query = query.Where(e => e.Sequence < position.BeforeSequence);
            var take = limit ?? 50;
            var entries = await query.OrderByDescending(e => e.Sequence).Take(take + 1).ToListAsync(ct);
            var more = entries.Count > take;
            if (more) entries.RemoveAt(entries.Count - 1);
            var next = more ? new AuditCursor(1, "management-audit", projectId, action, entries[^1].Sequence).Encode() : null;
            return Results.Ok(new { entries = entries.Select(e => new { e.Sequence, e.ProjectId, e.ActorUserId, e.PersonalKeyId, e.Action,
                e.ResourceType, e.ResourceId, e.RecordedAt, summary = JsonSerializer.Deserialize<JsonElement>(e.SummaryJson) }), nextCursor = next });
        });
        return app;
    }
}
