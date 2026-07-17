using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class CohortEndpoints
{
    public static IEndpointRouteBuilder MapCohortEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/cohorts");

        group.MapPost("/", async (
            Guid projectId,
            CreateCohortRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Cohort name is required."];
            }

            if (!Enum.TryParse<CohortType>(request.Type, ignoreCase: true, out var type))
            {
                errors["type"] = ["Type must be 'static' or 'dynamic'."];
            }

            var rulesJson = "[]";
            if (type == CohortType.Dynamic && errors.Count == 0)
            {
                rulesJson = request.Rules is { ValueKind: JsonValueKind.Array } rules
                    ? rules.GetRawText()
                    : string.Empty;

                if (!CohortRuleParser.TryParse(rulesJson, out var parsed, out var ruleError)
                    || parsed.Count == 0)
                {
                    errors["rules"] = [string.IsNullOrEmpty(ruleError)
                        ? "Dynamic cohorts need a non-empty 'rules' array."
                        : ruleError];
                }
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var cohort = new Cohort
            {
                ProjectId = projectId,
                Name = request.Name!.Trim(),
                Type = type,
                RulesJson = type == CohortType.Dynamic ? rulesJson : "[]",
            };
            db.Cohorts.Add(cohort);

            if (type == CohortType.Static && request.PersonIds is { Count: > 0 } personIds)
            {
                var known = await db.Persons
                    .Where(p => p.ProjectId == projectId && personIds.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync(ct);

                foreach (var personId in known.Distinct())
                {
                    db.CohortPersons.Add(new CohortPerson { CohortId = cohort.Id, PersonId = personId });
                }
            }

            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/cohorts/{cohort.Id}", ToResponse(cohort));
        });

        group.MapGet("/", async (
            Guid projectId,
            int? limit,
            int? offset,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            var cohorts = await db.Cohorts
                .Where(c => c.ProjectId == projectId)
                .OrderBy(c => c.CreatedAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(cohorts.Select(ToResponse));
        });

        group.MapGet("/{cohortId:guid}", async (
            Guid projectId,
            Guid cohortId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var cohort = await db.Cohorts
                .SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);

            return cohort is null ? Results.NotFound() : Results.Ok(ToResponse(cohort));
        });

        group.MapDelete("/{cohortId:guid}", async (
            Guid projectId,
            Guid cohortId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var cohort = await db.Cohorts
                .SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);

            if (cohort is null)
            {
                return Results.NotFound();
            }

            await db.CohortPersons.Where(cp => cp.CohortId == cohortId).ExecuteDeleteAsync(ct);
            db.Cohorts.Remove(cohort);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        group.MapGet("/{cohortId:guid}/persons", async (
            Guid projectId,
            Guid cohortId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CohortService cohortService,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            if (!await db.Cohorts.AnyAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct))
            {
                return Results.NotFound();
            }

            var members = await cohortService.GetMemberIdsAsync(projectId, cohortId, ct);
            var ordered = members.Order().ToList();

            return Results.Ok(new CohortMembersResponse(cohortId, ordered.Count, ordered));
        });

        group.MapPost("/{cohortId:guid}/persons", async (
            Guid projectId,
            Guid cohortId,
            ModifyCohortMembersRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var cohort = await db.Cohorts
                .SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);

            if (cohort is null)
            {
                return Results.NotFound();
            }

            if (cohort.Type != CohortType.Static)
            {
                return Results.Problem(
                    title: "Cohort is dynamic",
                    detail: "Members of dynamic cohorts are computed from rules and cannot be edited.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.PersonIds is not { Count: > 0 } personIds)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["personIds"] = ["Provide at least one person id."],
                });
            }

            var known = await db.Persons
                .Where(p => p.ProjectId == projectId && personIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);

            var existing = await db.CohortPersons
                .Where(cp => cp.CohortId == cohortId)
                .Select(cp => cp.PersonId)
                .ToListAsync(ct);

            foreach (var personId in known.Except(existing))
            {
                db.CohortPersons.Add(new CohortPerson { CohortId = cohortId, PersonId = personId });
            }

            await db.SaveChangesAsync(ct);

            var count = await db.CohortPersons.CountAsync(cp => cp.CohortId == cohortId, ct);
            return Results.Ok(new { added = known.Except(existing).Count(), total = count });
        });

        group.MapDelete("/{cohortId:guid}/persons/{personId:guid}", async (
            Guid projectId,
            Guid cohortId,
            Guid personId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var cohort = await db.Cohorts
                .SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);

            if (cohort is null)
            {
                return Results.NotFound();
            }

            if (cohort.Type != CohortType.Static)
            {
                return Results.Problem(
                    title: "Cohort is dynamic",
                    detail: "Members of dynamic cohorts are computed from rules and cannot be edited.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var row = await db.CohortPersons
                .SingleOrDefaultAsync(cp => cp.CohortId == cohortId && cp.PersonId == personId, ct);

            if (row is null)
            {
                return Results.NotFound();
            }

            db.CohortPersons.Remove(row);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return app;
    }

    private static CohortResponse ToResponse(Cohort cohort) =>
        new(
            cohort.Id,
            cohort.ProjectId,
            cohort.Name,
            cohort.Type.ToString().ToLowerInvariant(),
            JsonSerializer.Deserialize<JsonElement>(cohort.RulesJson),
            cohort.CreatedAt);
}
