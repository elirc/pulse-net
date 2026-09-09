using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class CohortEndpoints
{
    private static void MapCohortEditingFeatures(RouteGroupBuilder group)
    {
        group.MapPost("/preview", async (Guid projectId, PreviewCohortRequest request, HttpContext http,
            ProjectAccessService access, CohortEditingService edits, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (!CohortCandidateRules.TryValidate(request.Rules, out _, out var rules, out var error)) return InputRules.Problem("rules", error);
            var limit = request.SampleLimit ?? 10;
            if (limit is < 1 or > 50) return InputRules.Problem("sampleLimit", "Sample limit must be between 1 and 50.");
            return Results.Ok(await edits.PreviewAsync(projectId, rules, limit, ct));
        });

        group.MapPut("/{cohortId:guid}/rules", async (Guid projectId, Guid cohortId, ReplaceCohortRulesRequest request,
            HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var cohort = await db.Cohorts.SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);
            if (cohort is null) return Results.NotFound();
            if (cohort.Type != CohortType.Dynamic) return Results.Problem("Only dynamic cohorts have replaceable rules.", statusCode: 409);
            if (!CohortCandidateRules.TryValidate(request.Rules, out var json, out _, out var error)) return InputRules.Problem("rules", error);
            cohort.RulesJson = json;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(cohort));
        });

        group.MapPost("/{cohortId:guid}/snapshot", async (Guid projectId, Guid cohortId, SnapshotCohortRequest request,
            HttpContext http, ProjectAccessService access, CohortEditingService edits, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(request.Name, 200, "name", out var name, required: true) is { } invalid) return invalid;
            var result = await edits.SnapshotAsync(projectId, cohortId, name!, ct);
            if (result.Status == 404) return Results.NotFound();
            if (result.Status != 201) return Results.Problem(result.Detail, statusCode: result.Status);
            return Results.Created($"/api/projects/{projectId}/cohorts/{result.Cohort!.Id}",
                new { cohort = ToResponse(result.Cohort), result.MemberCount, result.EvaluatedAt });
        });

        group.MapPut("/{cohortId:guid}/persons", async (Guid projectId, Guid cohortId, ReplaceCohortMembersRequest request,
            HttpContext http, ProjectAccessService access, CohortEditingService edits, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.PersonIds is null || request.PersonIds.Count > 1000) return InputRules.Problem("personIds", "Supply up to 1,000 person IDs; an empty list clears membership.");
            var result = await edits.ReplaceMembersAsync(projectId, cohortId, request.PersonIds, ct);
            return result.Status switch
            {
                200 => Results.Ok(new { result.Added, result.Removed, result.Unchanged, result.Total }),
                404 => Results.NotFound(),
                400 => InputRules.Problem("personIds", "One or more people are unavailable in this project."),
                _ => Results.Problem("Only static cohorts have replaceable membership.", statusCode: 409),
            };
        });
    }
}
