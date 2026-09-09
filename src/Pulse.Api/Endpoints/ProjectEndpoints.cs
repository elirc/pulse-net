using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapPost("/{id:guid}/read-key/rotate", async (Guid id, RotateProjectReadKeyRequest request, HttpContext http,
            ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied) return denied;
            var expected = request.ExpectedReadKey?.Trim();
            if (expected is null || expected.Length != ApiKeyGenerator.ReadPrefix.Length + 32 ||
                !expected.StartsWith(ApiKeyGenerator.ReadPrefix, StringComparison.Ordinal) ||
                expected[ApiKeyGenerator.ReadPrefix.Length..].Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                return InputRules.Problem("expectedReadKey", "Supply the current read key in its generated format.");
            var next = ApiKeyGenerator.NewReadKey();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await ProjectMaintenance.AssertWritableAsync(db, id, ct);
            var changed = await db.Projects.Where(p => p.Id == id && p.ReadKey == expected)
                .ExecuteUpdateAsync(update => update.SetProperty(p => p.ReadKey, next), ct);
            if (changed == 0) return Results.Problem("The read key changed; inspect the current project before retrying.", statusCode: 409);
            await transaction.CommitAsync(ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { projectId = id, readKey = next });
        });

        group.MapPost("/", async (CreateProjectRequest request, HttpContext http, PulseDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Project name is required."],
                });
            }

            if (InputRules.Text(request.Name, 200, "name", out var name, required: true) is { } invalid)
                return invalid;

            var userId = ProjectAccessService.GetUserId(http.User)!.Value;

            var project = new Project
            {
                Name = name!,
                ApiKey = ApiKeyGenerator.NewKey(),
                ReadKey = ApiKeyGenerator.NewReadKey(),
            };

            db.Projects.Add(project);
            db.ProjectIngestionStates.Add(new ProjectIngestionState { ProjectId = project.Id });
            db.ProjectIngestionLeases.Add(new ProjectIngestionLease { ProjectId = project.Id });
            db.ProjectRetentionPolicies.Add(new ProjectRetentionPolicy { ProjectId = project.Id });
            var membership = new ProjectMembership
            {
                ProjectId = project.Id,
                UserId = userId,
                Role = ProjectRole.Admin,
            };
            db.ProjectMemberships.Add(membership);
            ManagementAuditWriter.Member(db, project.Id, membership.Id, AuthenticatedActor.From(http), null, membership.Role, membership.CreatedAt);
            await db.SaveChangesAsync();

            return Results.Created($"/api/projects/{project.Id}", ToResponse(project, ProjectRole.Admin));
        });

        group.MapGet("/", async (int? limit, int? offset, string? nameContains, string? sort, HttpContext http, PulseDbContext db, CancellationToken ct) =>
        {
            var userId = ProjectAccessService.GetUserId(http.User)!.Value;
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            if (InputRules.Text(nameContains, 200, "nameContains", out var nameFilter) is { } textError)
                return textError;
            if (InputRules.Choice(sort, "sort", ["oldest", "newest"], out var ordering) is { } sortError)
                return sortError;

            var query = (
                from p in db.Projects
                join m in db.ProjectMemberships on p.Id equals m.ProjectId
                where m.UserId == userId
                select new { Project = p, m.Role });
            if (RestrictedToken.IsRestricted(http.User))
            {
                var allowed = RestrictedToken.Projects(http.User);
                query = query.Where(row => allowed.Contains(row.Project.Id));
            }
            if (nameFilter is not null)
                query = query.Where(p => p.Project.Name.Contains(nameFilter));
            var ordered = ordering == "newest"
                ? query.OrderByDescending(p => p.Project.CreatedAt).ThenByDescending(p => p.Project.Id)
                : query.OrderBy(p => p.Project.CreatedAt).ThenBy(p => p.Project.Id);
            var projects = await ordered.Skip(skip).Take(take).ToListAsync(ct);

            return Results.Ok(projects.Select(row => ToResponse(row.Project, row.Role, RestrictedToken.IsRestricted(http.User))));
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
                : Results.Ok(ToResponse(project, await access.GetRoleAsync(http, id, ct) ?? ProjectRole.Viewer, RestrictedToken.IsRestricted(http.User)));
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateProjectRequest request,
            HttpContext http, PulseDbContext db, ProjectAccessService access, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied) return denied;
            var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == id, ct);
            if (project is null) return Results.NotFound();
            if (InputRules.Text(request.Name, 200, "name", out var name, required: true) is { } invalid)
                return invalid;
            project.Name = name!;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(project, await access.GetRoleAsync(http, id, ct) ?? ProjectRole.Viewer));
        });

        group.MapPost("/{id:guid}/members", async (
            Guid id,
            AddMemberRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            ProjectMembershipService memberships,
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

            var result = await memberships.InviteAsync(id, ProjectAccessService.GetUserId(http.User)!.Value, user.Id, ct, AuthenticatedActor.From(http).PersonalKeyId);
            if (result.Status != 201) return MembershipFailure(result.Status);
            var membership = result.Membership!;

            return Results.Created(
                $"/api/projects/{id}/members",
                new MemberResponse(user.Id, user.Email, user.Name, membership.CreatedAt, membership.Role.ToString().ToLowerInvariant()));
        });

        group.MapGet("/{id:guid}/members", async (
            Guid id,
            int? limit,
            int? offset,
            string? emailContains,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied)
            {
                return denied;
            }
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);

            if (InputRules.Text(emailContains, 320, "emailContains", out var emailFilter) is { } invalid)
                return invalid;
            emailFilter = emailFilter?.ToLowerInvariant();

            var members = await (
                from m in db.ProjectMemberships
                join u in db.Users on m.UserId equals u.Id
                where m.ProjectId == id && (emailFilter == null || u.Email.Contains(emailFilter))
                orderby m.CreatedAt, m.Id
                select new { u.Id, u.Email, u.Name, m.CreatedAt, m.Role })
                .Skip(skip).Take(take).ToListAsync(ct);

            return Results.Ok(members.Select(m => new MemberResponse(m.Id, m.Email, m.Name, m.CreatedAt, m.Role.ToString().ToLowerInvariant())));
        });

        group.MapPut("/{id:guid}/members/{userId:guid}/role", async (Guid id, Guid userId, ChangeMemberRoleRequest request,
            HttpContext http, ProjectAccessService access, ProjectMembershipService memberships, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied) return denied;
            if (InputRules.Choice(request.Role, "role", ["viewer", "editor", "admin"], out var name) is { } invalid) return invalid;
            if (name is null) return InputRules.Problem("role", "Role is required.");
            var role = name switch { "admin" => ProjectRole.Admin, "editor" => ProjectRole.Editor, _ => ProjectRole.Viewer };
            var result = await memberships.ChangeAsync(id, ProjectAccessService.GetUserId(http.User)!.Value, userId, role, ct, AuthenticatedActor.From(http).PersonalKeyId);
            if (result.Status != 200) return MembershipFailure(result.Status);
            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            return Results.Ok(new MemberResponse(user.Id, user.Email, user.Name, result.Membership!.CreatedAt, name));
        });

        group.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, HttpContext http,
            ProjectAccessService access, ProjectMembershipService memberships, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, id, ct) is { } denied) return denied;
            var result = await memberships.ChangeAsync(id, ProjectAccessService.GetUserId(http.User)!.Value, userId, null, ct, AuthenticatedActor.From(http).PersonalKeyId);
            return result.Status == 204 ? Results.NoContent() : MembershipFailure(result.Status);
        });

        return app;
    }

    private static RoleProjectResponse ToResponse(Project project, ProjectRole role, bool restricted = false) =>
        new(project.Id, project.Name, role.ToString().ToLowerInvariant(), project.CreatedAt,
            role == ProjectRole.Admin && !restricted ? project.ApiKey : null, role == ProjectRole.Admin && !restricted ? project.ReadKey : null);

    private static IResult MembershipFailure(int status) => status switch
    {
        404 => Results.NotFound(),
        409 => Results.Problem("A project must retain at least one administrator.", statusCode: 409),
        400 => InputRules.Problem("role", "Role must be viewer, editor, or admin."),
        _ => Results.Problem("Your current project role does not permit this operation.", statusCode: 403),
    };
}
