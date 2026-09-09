using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Api.Endpoints;

public static class DataManagementEndpoints
{
    public static IEndpointRouteBuilder MapDataManagementEndpoints(this IEndpointRouteBuilder app)
    {
        MapAnnotations(app);
        MapDefinitions(app);
        return app;
    }

    private static void MapAnnotations(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/annotations");

        group.MapPost("/", async (
            Guid projectId,
            CreateAnnotationRequest request,
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
            if (request.Date is null)
            {
                errors["date"] = ["Annotation date is required (YYYY-MM-DD)."];
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                errors["content"] = ["Annotation content is required."];
            }
            else if (request.Content.Trim().Length > 2000)
            {
                errors["content"] = ["Annotation content must contain at most 2000 characters after trimming."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var annotation = new Annotation
            {
                ProjectId = projectId,
                Date = request.Date!.Value,
                Content = request.Content!.Trim(),
            };

            db.Annotations.Add(annotation);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/annotations/{annotation.Id}", ToResponse(annotation));
        });

        group.MapGet("/", async (
            Guid projectId,
            DateOnly? from,
            DateOnly? to,
            string? contentContains,
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

            if (from is { } rangeStart && to is { } rangeEnd && rangeStart > rangeEnd)
                return InputRules.Problem("from", "from must not be after to.");
            if (InputRules.Text(contentContains, 100, "contentContains", out var contentFilter) is { } invalid)
                return invalid;

            var query = db.Annotations.Where(a => a.ProjectId == projectId);
            if (contentFilter is not null) query = query.Where(a => a.Content.Contains(contentFilter));
            if (from is { } first)
            {
                query = query.Where(a => a.Date >= first);
            }

            if (to is { } last)
            {
                query = query.Where(a => a.Date <= last);
            }

            var annotations = await query
                .OrderBy(a => a.Date)
                .ThenBy(a => a.CreatedAt)
                .ThenBy(a => a.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(annotations.Select(ToResponse));
        });

        group.MapPut("/{annotationId:guid}", async (
            Guid projectId,
            Guid annotationId,
            UpdateAnnotationRequest request,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var annotation = await db.Annotations
                .SingleOrDefaultAsync(a => a.ProjectId == projectId && a.Id == annotationId, ct);

            if (annotation is null)
            {
                return Results.NotFound();
            }

            if (request.Content is not null && (string.IsNullOrWhiteSpace(request.Content) || request.Content.Trim().Length > 2000))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["content"] = ["Annotation content must contain 1 to 2000 characters after trimming."],
                });
            }

            annotation.Date = request.Date ?? annotation.Date;
            annotation.Content = request.Content?.Trim() ?? annotation.Content;
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(annotation));
        });

        group.MapDelete("/{annotationId:guid}", async (
            Guid projectId,
            Guid annotationId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var annotation = await db.Annotations
                .SingleOrDefaultAsync(a => a.ProjectId == projectId && a.Id == annotationId, ct);

            if (annotation is null)
            {
                return Results.NotFound();
            }

            db.Annotations.Remove(annotation);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });
    }

    private static void MapDefinitions(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/event-definitions", async (
            Guid projectId,
            int? limit,
            int? offset,
            string? namePrefix,
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

            if (InputRules.Text(namePrefix, 200, "namePrefix", out var prefix) is { } invalid) return invalid;
            prefix = prefix?.ToLowerInvariant();

            var definitions = await db.EventDefinitions
                .Where(d => d.ProjectId == projectId)
                .Where(d => prefix == null || d.Name.ToLower().StartsWith(prefix))
                .OrderBy(d => d.Name)
                .Skip(skip)
                .Take(take)
                .Select(d => new EventDefinitionResponse(d.Name, d.FirstSeenAt, d.LastSeenAt))
                .ToListAsync(ct);

            return Results.Ok(definitions);
        });

        app.MapGet("/api/projects/{projectId:guid}/property-definitions", async (
            Guid projectId,
            int? limit,
            int? offset,
            string? type,
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

            if (InputRules.Choice(type, "type", ["string", "number", "boolean", "object", "array"], out var propertyType) is { } invalid)
                return invalid;

            var definitions = await db.PropertyDefinitions
                .Where(d => d.ProjectId == projectId)
                .Where(d => propertyType == null || d.PropertyType == propertyType)
                .OrderBy(d => d.Name)
                .Skip(skip)
                .Take(take)
                .Select(d => new PropertyDefinitionResponse(
                    d.Name, d.PropertyType, d.FirstSeenAt, d.LastSeenAt))
                .ToListAsync(ct);

            return Results.Ok(definitions);
        });
    }

    private static AnnotationResponse ToResponse(Annotation annotation) =>
        new(
            annotation.Id,
            annotation.ProjectId,
            annotation.Date,
            annotation.Content,
            annotation.CreatedAt);
}
