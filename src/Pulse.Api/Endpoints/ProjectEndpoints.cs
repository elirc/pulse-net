using Microsoft.EntityFrameworkCore;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/", async (CreateProjectRequest request, PulseDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Project name is required."],
                });
            }

            var project = new Project
            {
                Name = request.Name.Trim(),
                ApiKey = ApiKeyGenerator.NewKey(),
            };

            db.Projects.Add(project);
            await db.SaveChangesAsync();

            return Results.Created($"/api/projects/{project.Id}", ToResponse(project));
        });

        group.MapGet("/", async (PulseDbContext db) =>
        {
            var projects = await db.Projects
                .OrderBy(p => p.CreatedAt)
                .ToListAsync();

            return Results.Ok(projects.Select(ToResponse));
        });

        group.MapGet("/{id:guid}", async (Guid id, PulseDbContext db) =>
        {
            var project = await db.Projects.FindAsync(id);

            return project is null
                ? Results.NotFound()
                : Results.Ok(ToResponse(project));
        });

        return app;
    }

    private static ProjectResponse ToResponse(Project project) =>
        new(project.Id, project.Name, project.ApiKey, project.CreatedAt);
}
