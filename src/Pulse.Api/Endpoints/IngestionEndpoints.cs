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

            var letters = await db.DeadLetterEvents
                .Where(d => d.ProjectId == projectId)
                .OrderByDescending(d => d.FailedAt)
                .Take(take)
                .Select(d => new DeadLetterResponse(
                    d.Id, d.PayloadJson, d.Error, d.Attempts, d.FailedAt))
                .ToListAsync(ct);

            return Results.Ok(letters);
        });

        return app;
    }
}
