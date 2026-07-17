using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapPost("/", async (CreateProjectRequest request, HttpContext http, PulseDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Project name is required."],
                });
            }

            var userId = ProjectAccessService.GetUserId(http.User)!.Value;

            var project = new Project
            {
                Name = request.Name.Trim(),
                ApiKey = ApiKeyGenerator.NewKey(),
                ReadKey = ApiKeyGenerator.NewReadKey(),
            };

            db.Projects.Add(project);
            db.ProjectMemberships.Add(new ProjectMembership
            {
                ProjectId = project.Id,
                UserId = userId,
            });
            await db.SaveChangesAsync();

            return Results.Created($"/api/projects/{project.Id}", ToResponse(project));
        });

        group.MapGet("/", async (HttpContext http, PulseDbContext db) =>
        {
            var userId = ProjectAccessService.GetUserId(http.User)!.Value;

            var projects = await (
                from p in db.Projects
                join m in db.ProjectMemberships on p.Id equals m.ProjectId
                where m.UserId == userId
                orderby p.CreatedAt
                select p).ToListAsync();

            return Results.Ok(projects.Select(ToResponse));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied)
            {
                return denied;
            }

            var project = await db.Projects.FindAsync([id], ct);

            return project is null
                ? Results.NotFound()
                : Results.Ok(ToResponse(project));
        });

        group.MapPost("/{id:guid}/members", async (
            Guid id,
            AddMemberRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied)
            {
                return denied;
            }

            var email = request.Email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["email"] = ["Member email is required."],
                });
            }

            var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);
            if (user is null)
            {
                return Results.Problem(
                    title: "No such user",
                    detail: $"No account exists for {email}.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var membership = await db.ProjectMemberships
                .SingleOrDefaultAsync(m => m.ProjectId == id && m.UserId == user.Id, ct);

            if (membership is null)
            {
                membership = new ProjectMembership { ProjectId = id, UserId = user.Id };
                db.ProjectMemberships.Add(membership);
                await db.SaveChangesAsync(ct);
            }

            return Results.Created(
                $"/api/projects/{id}/members",
                new MemberResponse(user.Id, user.Email, user.Name, membership.CreatedAt));
        });

        group.MapGet("/{id:guid}/members", async (
            Guid id,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied)
            {
                return denied;
            }

            var members = await (
                from m in db.ProjectMemberships
                join u in db.Users on m.UserId equals u.Id
                where m.ProjectId == id
                orderby m.CreatedAt
                select new MemberResponse(u.Id, u.Email, u.Name, m.CreatedAt)).ToListAsync(ct);

            return Results.Ok(members);
        });

        return app;
    }

    private static ProjectResponse ToResponse(Project project) =>
        new(project.Id, project.Name, project.ApiKey, project.ReadKey, project.CreatedAt);
}
