using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class FeatureFlagEndpoints
{
    private static void MapFlagGovernanceFeatures(RouteGroupBuilder group)
    {
        group.MapGet("/{key}/versions", async (Guid projectId, string key, long? beforeRevision, int? limit, HttpContext http,
            ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
            if (flag is null) return Results.NotFound();
            if (beforeRevision is <= 0 || limit is < 1 or > 100) return InputRules.Problem("pagination", "Use a positive beforeRevision and limit from 1 to 100.");
            var take = limit ?? 20;
            var versions = await db.FlagVersions.AsNoTracking().Where(v => v.ProjectId == projectId && v.FlagId == flag.Id && (beforeRevision == null || v.Revision < beforeRevision))
                .OrderByDescending(v => v.Revision).Take(take + 1).ToListAsync(ct);
            var more = versions.Count > take;
            if (more) versions.RemoveAt(versions.Count - 1);
            return Results.Ok(new { versions = versions.Select(v => new { v.Revision, config = JsonSerializer.Deserialize<FlagConfiguration>(v.ConfigJson, JsonSerializerOptions.Web),
                v.RecordedAt, v.ActorUserId, v.PersonalKeyId, v.Origin, v.RestoredFromRevision }), nextBeforeRevision = more ? (long?)versions[^1].Revision : null });
        });

        group.MapPost("/{key}/restore", async (Guid projectId, string key, RestoreFlagRequest request, HttpContext http,
            ProjectAccessService access, PulseDbContext db, FlagMutationService mutations, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
            if (flag is null) return Results.NotFound();
            if (FlagPrecondition.Require(http, flag, out var revision) is { } precondition) return precondition;
            if (request.TargetRevision < 1) return InputRules.Problem("targetRevision", "Supply a positive retained revision.");
            var result = await mutations.RestoreAsync(flag, request.TargetRevision, revision, AuthenticatedActor.From(http), ct);
            if (result.Status != 200) return FlagFailure(result.Status);
            http.Response.Headers.ETag = FlagPrecondition.ETag(result.Flag!);
            return Results.Ok(ToResponse(result.Flag!));
        });

        group.MapPost("/{key}/rollout-schedule", async (Guid projectId, string key, ScheduleFlagRequest request, HttpContext http,
            ProjectAccessService access, FlagScheduleService schedules, IConfiguration configuration, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!configuration.GetValue("FlagScheduling:AcceptNew", true)) return Results.Problem("New rollout schedules are paused.", statusCode: 503);
            var result = await schedules.CreateAsync(projectId, key, request.ExecuteAt, request.RolloutPercentage, request.ExpectedRevision, AuthenticatedActor.From(http), ct);
            return result.Status == 202 ? Results.Accepted($"/api/projects/{projectId}/feature-flags/{key}/rollout-schedule", ScheduleResponse(result.Schedule!)) : FlagFailure(result.Status);
        });

        group.MapGet("/{key}/rollout-schedule", async (Guid projectId, string key, HttpContext http,
            ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
            if (flag is null) return Results.NotFound();
            var schedule = await db.FlagRolloutSchedules.AsNoTracking().SingleOrDefaultAsync(s => s.ProjectId == projectId && s.FlagId == flag.Id, ct);
            return schedule is null ? Results.NotFound() : Results.Ok(ScheduleResponse(schedule));
        });

        group.MapDelete("/{key}/rollout-schedule", async (Guid projectId, string key, HttpContext http,
            ProjectAccessService access, PulseDbContext db, FlagScheduleService schedules, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var flag = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
            if (flag is null) return Results.NotFound();
            var result = await schedules.CancelAsync(projectId, flag.Id, AuthenticatedActor.From(http), ct);
            return result == 204 ? Results.NoContent() : FlagFailure(result);
        });
    }

    private static object ScheduleResponse(FlagRolloutSchedule schedule) => new { schedule.Id, schedule.ProjectId, schedule.FlagId,
        schedule.CreatorUserId, schedule.CreatedAt, schedule.ExecuteAt, schedule.RolloutPercentage, schedule.ExpectedRevision,
        status = schedule.Status.ToString().ToLowerInvariant(), schedule.CompletedAt, schedule.Reason, schedule.AppliedRevision };
}
