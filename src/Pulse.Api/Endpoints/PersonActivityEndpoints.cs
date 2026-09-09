using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class PersonEndpoints
{
    private static void MapPersonActivityFeatures(RouteGroupBuilder group)
    {
        group.MapGet("/{personId:guid}/events", async (Guid projectId, Guid personId, int? limit, string? cursor, string? @event,
            HttpContext http, ProjectAccessService access, PulseDbContext db, PersonActivityService activity, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!await db.Persons.AnyAsync(p => p.ProjectId == projectId && p.Id == personId, ct)) return Results.NotFound();
            var take = limit ?? 50;
            if (take is < 1 or > 200) return InputRules.Problem("limit", "Limit must be between 1 and 200.");
            if (InputRules.Text(@event, 200, "event", out var name) is { } invalid) return invalid;
            if (!ScopedCursor.TryDecode(cursor, "person-timeline", projectId, personId, name, out var position))
                return InputRules.Problem("cursor", "Invalid cursor or cursor belongs to a different query.");
            return Results.Ok(await activity.TimelineAsync(projectId, personId, name, take, position, ct));
        });

        group.MapGet("/{personId:guid}/activity-summary", async (Guid projectId, Guid personId, DateTimeOffset? from, DateTimeOffset? to,
            HttpContext http, ProjectAccessService access, PulseDbContext db, PersonActivityService activity, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!await db.Persons.AnyAsync(p => p.ProjectId == projectId && p.Id == personId, ct)) return Results.NotFound();
            if (from is null || to is null || from >= to || (to.Value - from.Value).TotalDays > 90)
                return InputRules.Problem("from", "Supply explicit from/to with 0 < duration <= 90 days; to is exclusive.");
            return Results.Ok(await activity.SummaryAsync(projectId, personId, from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), ct));
        });
    }
}
