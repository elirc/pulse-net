using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static class InsightEndpoints
{
    public static IEndpointRouteBuilder MapInsightEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/insights");

        group.MapGet("/trend", async (
            Guid projectId,
            string? @event,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? interval,
            PulseDbContext db,
            QueryService queries,
            CancellationToken ct) =>
        {
            if (!await ProjectExistsAsync(db, projectId, ct))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(@event))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["event"] = ["Query parameter 'event' is required."],
                });
            }

            if (!TryParseInterval(interval, out var parsedInterval))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["interval"] = ["Interval must be one of: hour, day, week."],
                });
            }

            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-30);
            if (start > end)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["from"] = ["'from' must not be after 'to'."],
                });
            }

            var result = await queries.TrendAsync(projectId, @event.Trim(), start, end, parsedInterval, ct);
            return Results.Ok(result);
        });

        group.MapPost("/funnel", async (
            Guid projectId,
            FunnelRequest request,
            PulseDbContext db,
            QueryService queries,
            CancellationToken ct) =>
        {
            if (!await ProjectExistsAsync(db, projectId, ct))
            {
                return Results.NotFound();
            }

            var steps = request.Steps?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList() ?? [];

            if (steps.Count < 2)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["steps"] = ["A funnel needs at least two steps."],
                });
            }

            var windowDays = request.WindowDays ?? 14;
            if (windowDays is < 1 or > 90)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["windowDays"] = ["windowDays must be between 1 and 90."],
                });
            }

            var end = request.To ?? DateTimeOffset.UtcNow;
            var start = request.From ?? end.AddDays(-30);

            var result = await queries.FunnelAsync(projectId, steps, start, end, windowDays, ct);
            return Results.Ok(result);
        });

        group.MapGet("/retention", async (
            Guid projectId,
            DateOnly? from,
            int? days,
            string? targetEvent,
            PulseDbContext db,
            QueryService queries,
            CancellationToken ct) =>
        {
            if (!await ProjectExistsAsync(db, projectId, ct))
            {
                return Results.NotFound();
            }

            var windowDays = days ?? 7;
            if (windowDays is < 1 or > 60)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["days must be between 1 and 60."],
                });
            }

            var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(windowDays - 1));

            var result = await queries.RetentionAsync(projectId, start, windowDays, targetEvent, ct);
            return Results.Ok(result);
        });

        group.MapPost("/", async (
            Guid projectId,
            SaveInsightRequest request,
            PulseDbContext db,
            CancellationToken ct) =>
        {
            if (!await ProjectExistsAsync(db, projectId, ct))
            {
                return Results.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors["name"] = ["Insight name is required."];
            }

            if (!Enum.TryParse<InsightType>(request.Type, ignoreCase: true, out var type))
            {
                errors["type"] = ["Type must be one of: trend, funnel, retention."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var insight = new Insight
            {
                ProjectId = projectId,
                Name = request.Name!.Trim(),
                Type = type,
                ConfigJson = request.Config is { ValueKind: JsonValueKind.Object } config
                    ? config.GetRawText()
                    : "{}",
            };

            db.Insights.Add(insight);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/projects/{projectId}/insights/{insight.Id}",
                ToResponse(insight));
        });

        group.MapGet("/", async (Guid projectId, PulseDbContext db, CancellationToken ct) =>
        {
            if (!await ProjectExistsAsync(db, projectId, ct))
            {
                return Results.NotFound();
            }

            var insights = await db.Insights
                .Where(i => i.ProjectId == projectId)
                .OrderBy(i => i.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(insights.Select(ToResponse));
        });

        group.MapGet("/{insightId:guid}", async (
            Guid projectId,
            Guid insightId,
            PulseDbContext db,
            CancellationToken ct) =>
        {
            var insight = await db.Insights
                .SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);

            return insight is null ? Results.NotFound() : Results.Ok(ToResponse(insight));
        });

        return app;
    }

    private static Task<bool> ProjectExistsAsync(PulseDbContext db, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId, ct);

    private static bool TryParseInterval(string? raw, out TrendInterval interval)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            interval = TrendInterval.Day;
            return true;
        }

        return Enum.TryParse(raw, ignoreCase: true, out interval)
               && Enum.IsDefined(interval);
    }

    private static InsightResponse ToResponse(Insight insight) =>
        new(
            insight.Id,
            insight.ProjectId,
            insight.Name,
            insight.Type.ToString().ToLowerInvariant(),
            JsonSerializer.Deserialize<JsonElement>(insight.ConfigJson),
            insight.CreatedAt);
}
