using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Api.Auth;
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
            string? filters,
            string? breakdown,
            int? breakdownLimit,
            HttpContext http,
            PulseDbContext db,
            QueryService queries,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied)
            {
                return denied;
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

            if (!FilterParsing.TryParseJson(filters, out var parsedFilters, out var filterError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["filters"] = [filterError],
                });
            }

            if (string.IsNullOrWhiteSpace(breakdown))
            {
                var result = await queries.TrendAsync(
                    projectId, @event.Trim(), start, end, parsedInterval, parsedFilters, ct);
                return Results.Ok(result);
            }

            var limit = breakdownLimit ?? 5;
            if (limit is < 1 or > 25)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["breakdownLimit"] = ["breakdownLimit must be between 1 and 25."],
                });
            }

            var breakdownResult = await queries.TrendBreakdownAsync(
                projectId, @event.Trim(), start, end, parsedInterval,
                breakdown.Trim(), limit, parsedFilters, ct);
            return Results.Ok(breakdownResult);
        });

        group.MapPost("/funnel", async (
            Guid projectId,
            FunnelRequest request,
            HttpContext http,
            PulseDbContext db,
            QueryService queries,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied)
            {
                return denied;
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

            if (!FilterParsing.TryConvert(request.Filters, out var parsedFilters, out var filterError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["filters"] = [filterError],
                });
            }

            var end = request.To ?? DateTimeOffset.UtcNow;
            var start = request.From ?? end.AddDays(-30);

            var result = await queries.FunnelAsync(
                projectId, steps, start, end, windowDays, parsedFilters, ct);
            return Results.Ok(result);
        });

        group.MapGet("/retention", async (
            Guid projectId,
            DateOnly? from,
            int? days,
            string? targetEvent,
            string? filters,
            HttpContext http,
            PulseDbContext db,
            QueryService queries,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var windowDays = days ?? 7;
            if (windowDays is < 1 or > 60)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["days"] = ["days must be between 1 and 60."],
                });
            }

            if (!FilterParsing.TryParseJson(filters, out var parsedFilters, out var filterError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["filters"] = [filterError],
                });
            }

            var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(windowDays - 1));

            var result = await queries.RetentionAsync(
                projectId, start, windowDays, targetEvent, parsedFilters, ct);
            return Results.Ok(result);
        });

        group.MapPost("/", async (
            Guid projectId,
            SaveInsightRequest request,
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

        group.MapGet("/", async (
            Guid projectId,
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

            var insights = await db.Insights
                .Where(i => i.ProjectId == projectId)
                .OrderBy(i => i.CreatedAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);

            return Results.Ok(insights.Select(ToResponse));
        });

        group.MapGet("/{insightId:guid}", async (
            Guid projectId,
            Guid insightId,
            HttpContext http,
            PulseDbContext db,
            ProjectAccessService access,
            CancellationToken ct) =>
        {
            if (await access.RequireMemberAsync(http, projectId, ct) is { } denied)
            {
                return denied;
            }

            var insight = await db.Insights
                .SingleOrDefaultAsync(i => i.ProjectId == projectId && i.Id == insightId, ct);

            return insight is null ? Results.NotFound() : Results.Ok(ToResponse(insight));
        });

        return app;
    }

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
