using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public record DiscardErasureItemsRequest(IReadOnlyList<ErasureDiscardSelection>? Items);

public static class ErasureEndpoints
{
    public static IEndpointRouteBuilder MapErasureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:guid}/persons/{personId:guid}/erasure", InitiateAsync);
        // The old DELETE now starts exactly the same workflow and returns 202.
        app.MapDelete("/api/projects/{projectId:guid}/persons/{personId:guid}", InitiateAsync);
        var group = app.MapGroup("/api/projects/{projectId:guid}/erasure-jobs");
        group.MapGet("/{jobId:guid}", async (Guid projectId, Guid jobId, HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            http.Response.Headers.CacheControl = "no-store";
            var job = await db.ErasureJobs.AsNoTracking().SingleOrDefaultAsync(j => j.ProjectId == projectId && j.Id == jobId, ct);
            if (job is null) return Results.NotFound();
            var unreadable = await db.ErasureUnreadableItems.AsNoTracking().Where(i => i.ProjectId == projectId && i.JobId == jobId && i.DiscardedAt == null)
                .OrderBy(i => i.ObservedAt).ThenBy(i => i.Id).Select(i => new { i.QueueSequence, i.DeadLetterId, i.ContentHash, i.ObservedAt }).Take(100).ToListAsync(ct);
            return Results.Ok(new { job = Metadata(job), unreadable, remainingReviewItems = await db.ErasureUnreadableItems.CountAsync(i => i.ProjectId == projectId && i.JobId == jobId && i.DiscardedAt == null, ct) });
        });
        group.MapPost("/{jobId:guid}/resume", async (Guid projectId, Guid jobId, HttpContext http, ProjectAccessService access, PersonErasureService erasure, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return Response(await erasure.ResumeAsync(projectId, jobId, ProjectAccessService.GetUserId(http.User)!.Value, ct));
        });
        group.MapPost("/{jobId:guid}/discard-unreadable", async (Guid projectId, Guid jobId, DiscardErasureItemsRequest request, HttpContext http,
            ProjectAccessService access, PersonErasureService erasure, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return Response(await erasure.DiscardUnreadableAsync(projectId, jobId, ProjectAccessService.GetUserId(http.User)!.Value, request.Items ?? [], ct));
        });
        return app;
    }

    private static async Task<IResult> InitiateAsync(Guid projectId, Guid personId, HttpContext http, ProjectAccessService access, PersonErasureService erasure, CancellationToken ct)
    {
        if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
        http.Response.Headers.CacheControl = "no-store";
        return Response(await erasure.InitiateAsync(projectId, personId, ProjectAccessService.GetUserId(http.User)!.Value, ct));
    }

    private static IResult Response(ErasureOutcome outcome) => outcome.Status switch
    {
        202 => Results.Accepted($"/api/projects/{outcome.Job!.ProjectId}/erasure-jobs/{outcome.Job.Id}", new { job = Metadata(outcome.Job),
            statusUrl = $"/api/projects/{outcome.Job.ProjectId}/erasure-jobs/{outcome.Job.Id}", invalidatesAllProjectExports = true }),
        404 => Results.NotFound(),
        _ => Results.Problem(outcome.Code ?? "Erasure operation is not permitted.", statusCode: outcome.Status),
    };
    private static object Metadata(ErasureJob job) => new { job.Id, job.ProjectId, job.PersonId,
        status = job.Status == ErasureJobStatus.NeedsReview ? "needsReview" : job.Status.ToString().ToLowerInvariant(), phase = job.Phase.ToString(),
        job.CreatedAt, job.UpdatedAt, job.CompletedAt, job.ErrorCode, job.RemovedEvents, job.RemovedQueueItems, job.RemovedDeadLetters,
        job.RemovedAliases, job.RemovedCohortLinks, job.InvalidatedExports };
}
