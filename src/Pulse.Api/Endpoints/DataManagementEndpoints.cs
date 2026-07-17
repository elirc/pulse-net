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
        MapPersonDeletion(app);
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
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var query = db.Annotations.Where(a => a.ProjectId == projectId);
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

            if (request.Content is not null && string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["content"] = ["Annotation content must not be blank."],
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
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var definitions = await db.EventDefinitions
                .Where(d => d.ProjectId == projectId)
                .OrderBy(d => d.Name)
                .Select(d => new EventDefinitionResponse(d.Name, d.FirstSeenAt, d.LastSeenAt))
                .ToListAsync(ct);

            return Results.Ok(definitions);
        });

        app.MapGet("/api/projects/{projectId:guid}/property-definitions", async (
            Guid projectId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var definitions = await db.PropertyDefinitions
                .Where(d => d.ProjectId == projectId)
                .OrderBy(d => d.Name)
                .Select(d => new PropertyDefinitionResponse(
                    d.Name, d.PropertyType, d.FirstSeenAt, d.LastSeenAt))
                .ToListAsync(ct);

            return Results.Ok(definitions);
        });
    }

    private static void MapPersonDeletion(IEndpointRouteBuilder app)
    {
        // GDPR-style purge: the person, their distinct-id mappings, their
        // events and their cohort memberships all go.
        app.MapDelete("/api/projects/{projectId:guid}/persons/{personId:guid}", async (
            Guid projectId,
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

            var person = await db.Persons
                .SingleOrDefaultAsync(p => p.ProjectId == projectId && p.Id == personId, ct);

            if (person is null)
            {
                return Results.NotFound();
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var deletedEvents = await db.Events
                .Where(e => e.ProjectId == projectId && e.PersonId == personId)
                .ExecuteDeleteAsync(ct);

            var deletedDistinctIds = await db.PersonDistinctIds
                .Where(m => m.ProjectId == projectId && m.PersonId == personId)
                .ExecuteDeleteAsync(ct);

            await db.CohortPersons
                .Where(cp => cp.PersonId == personId)
                .ExecuteDeleteAsync(ct);

            db.Persons.Remove(person);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new PersonDeletionResponse(personId, deletedEvents, deletedDistinctIds));
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
