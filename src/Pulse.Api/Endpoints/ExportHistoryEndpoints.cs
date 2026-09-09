using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class ExportEndpoints
{
    private static void MapExportHistoryFeatures(RouteGroupBuilder group)
    {
        group.MapPost("/exports/{jobId:guid}/cancel", async (Guid projectId, Guid jobId, HttpContext http,
            ProjectAccessService access, ExportOwnershipService ownership, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var result = await ownership.CancelAsync(projectId, jobId, ct);
            return result.Status switch
            {
                404 => Results.NotFound(),
                409 => Results.Problem("Completed and failed exports cannot be cancelled.", statusCode: 409),
                _ => Results.Json(new { jobId, status = ExportDocument.StateName(result.State!.Value) }, statusCode: result.Status),
            };
        });
        group.MapPost("/exports/{jobId:guid}/retry", async (Guid projectId, Guid jobId, HttpContext http,
            ProjectAccessService access, ExportJobOperationsService jobs, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var result = await jobs.RetryAsync(projectId, jobId, ct);
            return result.Status switch
            {
                202 => Results.Accepted($"/api/projects/{projectId}/exports/{result.Job!.Id}", new { sourceJobId = jobId, job = ToResponse(result.Job) }),
                404 => Results.NotFound(),
                _ => Results.Problem("Only failed exports can be retried as new jobs.", statusCode: 409),
            };
        });

        group.MapGet("/exports", async (Guid projectId, string? status, string? type, int? limit, string? cursor,
            HttpContext http, ProjectAccessService access, ExportJobOperationsService jobs, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Choice(status, "status", ["pending", "running", "completed", "failed", "cancelrequested", "cancelled"], out var state) is { } invalid) return invalid;
            if (InputRules.Choice(type, "type", ["events", "persons", "insight"], out var kind) is { } badType) return badType;
            var take = limit ?? 25;
            if (take is < 1 or > 100) return InputRules.Problem("limit", "Limit must be between 1 and 100.");
            var filter = (state ?? "") + ":" + (kind ?? "");
            if (!ScopedCursor.TryDecode(cursor, "export-history", projectId, null, filter, out var position)) return CursorProblem();
            ExportJobStatus? parsed = state is null ? null : Enum.Parse<ExportJobStatus>(state, true);
            var result = await jobs.HistoryAsync(projectId, parsed, kind, take, filter, position, ct);
            return Results.Ok(new { jobs = result.Jobs.Select(ToResponse), result.NextCursor });
        });

        group.MapDelete("/exports/{jobId:guid}", async (Guid projectId, Guid jobId, HttpContext http,
            ProjectAccessService access, ExportJobOperationsService jobs, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return await jobs.DeleteAsync(projectId, jobId, ct) switch
            {
                204 => Results.NoContent(), 404 => Results.NotFound(),
                _ => Results.Problem("Only terminal exports can be deleted.", statusCode: 409),
            };
        });

        group.MapGet("/exports/{jobId:guid}/integrity", async (Guid projectId, Guid jobId, HttpContext http,
            ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var job = await db.ExportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);
            if (job is null) return Results.NotFound();
            if (job.Status != ExportJobStatus.Completed || job.ResultContent is null)
                return Results.Problem("A completed stored document is required.", statusCode: 409);
            return Results.Ok(ExportDocument.Integrity(job));
        });
    }

    private static ExportJobResponse ToResponse(ExportJobMetadata job) => new(job.Id, job.ProjectId, job.Type, job.Format,
        ExportDocument.StateName(job.Status), job.RowCount, job.Error, job.CreatedAt, job.CompletedAt,
        job.Consistency, job.SnapshotCapturedAt, job.AttemptGeneration, job.LastHeartbeatAt);
}
