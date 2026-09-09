using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public record UpdateRetentionRequest(bool Enabled, int Days, long? ExpectedRevision);

public static class RetentionEndpoints
{
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/retention");
        group.MapGet("", async (Guid projectId, HttpContext http, ProjectAccessService access, EventRetentionService retention, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return Results.Ok(await retention.GetAsync(projectId, ct));
        });
        group.MapPut("", async (Guid projectId, UpdateRetentionRequest request, HttpContext http, ProjectAccessService access, EventRetentionService retention, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.ExpectedRevision is null) return Results.Problem("Supply expectedRevision from the policy you read.", statusCode: 428);
            var outcome = await retention.UpdateAsync(projectId, request.Enabled, request.Days, request.ExpectedRevision.Value, ct);
            return outcome.Status switch
            {
                200 => Results.Ok(outcome.Policy),
                412 => Results.Problem("The retention policy changed. Read it again before deciding how to update it.", statusCode: 412),
                _ => InputRules.Problem("days", "Days must be 30–3650 and expectedRevision must be a positive, incrementable integer."),
            };
        });
        group.MapGet("/preview", async (Guid projectId, HttpContext http, ProjectAccessService access, EventRetentionService retention, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return Results.Ok(await retention.PreviewAsync(projectId, ct));
        });
        group.MapGet("/runs", async (Guid projectId, HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var runs = await db.RetentionRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(50).ToListAsync(ct);
            return Results.Ok(runs.Select(r => new { r.Id, r.PolicyRevision, r.Cutoff, r.StartedAt, r.CompletedAt, status = r.Status.ToString().ToLowerInvariant(), r.RemovedCount, r.Batches, r.LastBatchAt }));
        });
        return app;
    }
}
