using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:guid}/capture-receipts/{receiptId:guid}", async (Guid projectId, Guid receiptId, HttpContext http,
            ProjectAccessService access, CaptureReceiptService receipts, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            http.Response.Headers.CacheControl = "no-store";
            var receipt = await receipts.ReadAsync(projectId, receiptId, ct);
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        });
        app.MapGet("/api/projects/{projectId:guid}/ingestion/limits", async (Guid projectId, HttpContext http,
            ProjectAccessService access, QueueAdmissionService admission, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            return Results.Ok(await admission.ReadLimitsAsync(projectId, ct));
        });
        app.MapPut("/api/projects/{projectId:guid}/ingestion/limits", async (Guid projectId, UpdateIngestionLimitsRequest request, HttpContext http,
            ProjectAccessService access, QueueAdmissionService admission, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.MaxPending is < 1 or > 100000) return InputRules.Problem("maxPending", "Use null to disable the limit or an integer from 1 to 100000.");
            return Results.Ok(await admission.SetLimitAsync(projectId, request.MaxPending, ct));
        });
        app.MapGet("/api/projects/{projectId:guid}/ingestion/dead-letters/{letterId:guid}/replay-check", async (
            Guid projectId, Guid letterId, HttpContext http, ProjectAccessService access, PulseDbContext db, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var payload = await db.DeadLetterEvents.AsNoTracking().Where(d => d.ProjectId == projectId && d.Id == letterId)
                .Select(d => d.PayloadJson).SingleOrDefaultAsync(ct);
            if (payload is null) return Results.NotFound();
            var result = ReplayEnvelopeValidator.Validate(payload);
            return Results.Ok(new { letterId, result.Replayable, result.Issues });
        });

        app.MapPost("/api/projects/{projectId:guid}/ingestion/dead-letters/replay-batch", async (
            Guid projectId, ReplayDeadLettersRequest request, HttpContext http, ProjectAccessService access, IngestionOperationsService operations, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            if (request.LetterIds is not { Count: >= 1 and <= 20 } ids || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
                return InputRules.Problem("letterIds", "Supply between 1 and 20 unique nonempty letter IDs.");
            return Results.Ok(await operations.ReplayBatchAsync(projectId, ids, ct));
        });

        // Operational metrics, /health-style (no auth): queue depth, dead
        // letters, and process-lifetime throughput counters.
        app.MapGet("/api/ingestion/metrics", async (
            PulseDbContext db,
            IngestionCounters counters,
            CancellationToken ct) =>
        {
            var pending = await db.QueuedEvents.CountAsync(ct);
            var deadLetters = await db.DeadLetterEvents.CountAsync(ct);

            return Results.Ok(new IngestionMetricsResponse(
                pending,
                deadLetters,
                counters.Processed,
                counters.DeadLettered));
        });

        // Per-project dead-letter inspection, member-only.
        app.MapGet("/api/projects/{projectId:guid}/ingestion/dead-letters", async (
            Guid projectId,
            int? limit,
            int? offset,
            string? errorContains,
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
            if (InputRules.Text(errorContains, 100, "errorContains", out var errorFilter) is { } invalid)
                return invalid;

            var letters = await db.DeadLetterEvents
                .Where(d => d.ProjectId == projectId)
                .Where(d => errorFilter == null || d.Error.Contains(errorFilter))
                .OrderByDescending(d => d.FailedAt)
                .ThenByDescending(d => d.Id)
                .Skip(skip)
                .Take(take)
                .Select(d => new DeadLetterResponse(
                    d.Id, d.PayloadJson, d.Error, d.Attempts, d.FailedAt))
                .ToListAsync(ct);

            return Results.Ok(letters);
        });

        app.MapGet("/api/projects/{projectId:guid}/ingestion/dead-letters/{letterId:guid}", async (
            Guid projectId, Guid letterId, HttpContext http, PulseDbContext db,
            ProjectAccessService access, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied) return denied;
            var letter = await db.DeadLetterEvents
                .Where(d => d.ProjectId == projectId && d.Id == letterId)
                .Select(d => new DeadLetterResponse(d.Id, d.PayloadJson, d.Error, d.Attempts, d.FailedAt))
                .SingleOrDefaultAsync(ct);
            return letter is null ? Results.NotFound() : Results.Ok(letter);
        });

        app.MapGet("/api/projects/{projectId:guid}/ingestion/metrics", async (
            Guid projectId, HttpContext http, ProjectAccessService access,
            IngestionOperationsService operations, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            return Results.Ok(await operations.GetMetricsAsync(projectId, ct));
        });

        app.MapPost("/api/projects/{projectId:guid}/ingestion/dead-letters/{letterId:guid}/replay", async (
            Guid projectId, Guid letterId, HttpContext http, ProjectAccessService access,
            IngestionOperationsService operations, CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            return await operations.ReplayAsync(projectId, letterId, ct) switch
            {
                DeadLetterReplayOutcome.Queued => Results.Accepted(value: new CaptureResponse("queued", 1)),
                DeadLetterReplayOutcome.NotFound => Results.NotFound(),
                DeadLetterReplayOutcome.CapacityExceeded => CaptureEndpoints.AdmissionFailure(http, 429, "queue_capacity_exceeded"),
                DeadLetterReplayOutcome.Paused => CaptureEndpoints.AdmissionFailure(http, 503, "project_maintenance"),
                DeadLetterReplayOutcome.Suppressed => CaptureEndpoints.AdmissionFailure(http, 422, "identity_suppressed"),
                _ => Results.Problem(title: "Dead letter cannot be replayed",
                    detail: "The stored payload must contain an event name, distinct id and valid object properties. The dead letter was retained.",
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        });

        return app;
    }
}
