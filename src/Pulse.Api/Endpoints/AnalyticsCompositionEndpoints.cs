using Pulse.Api.Auth;
using Pulse.Api.Contracts;
using Pulse.Domain;
using Pulse.Infrastructure.Services;

namespace Pulse.Api.Endpoints;

public static partial class InsightEndpoints
{
    private static void MapAnalyticsCompositionFeatures(RouteGroupBuilder group)
    {
        group.MapGet("/period-comparison", async (Guid projectId, string? @event, DateTimeOffset? from, DateTimeOffset? to,
            HttpContext http, ProjectAccessService access, AnalyticsCompositionService analytics, CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied) return denied;
            if (InputRules.Text(@event, 200, "event", out var name, required: true) is { } invalid) return invalid;
            if (from is null || to is null || from >= to || (to.Value - from.Value).TotalDays > 90)
                return InputRules.Problem("from", "Supply explicit from/to bounds with 0 < duration <= 90 days.");
            DateTimeOffset previousFrom;
            try { previousFrom = from.Value - (to.Value - from.Value); }
            catch (ArgumentOutOfRangeException) { return InputRules.Problem("from", "Previous period falls outside the supported timestamp range."); }
            return Results.Ok(await analytics.CompareAsync(projectId, name!, from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), previousFrom.ToUniversalTime(), ct));
        });

        group.MapPost("/multi-trend", async (Guid projectId, MultiTrendRequest request,
            HttpContext http, ProjectAccessService access, AnalyticsCompositionService analytics, CancellationToken ct) =>
        {
            if (await access.RequireReadAsync(http, projectId, ct) is { } denied) return denied;
            if (request.Events is not { Count: >= 1 and <= 5 } inputs) return InputRules.Problem("events", "Supply between 1 and 5 event names.");
            var names = new List<string>();
            for (var index = 0; index < inputs.Count; index++)
            {
                if (InputRules.Text(inputs[index], 200, $"events[{index}]", out var name, required: true) is { } invalid) return invalid;
                if (names.Contains(name!, StringComparer.Ordinal)) return InputRules.Problem($"events[{index}]", "Event names must be unique after trimming.");
                names.Add(name!);
            }
            if (InputRules.Choice(request.Interval, "interval", ["hour", "day", "week"], out var interval) is { } badInterval) return badInterval;
            if (request.From is null || request.To is null || request.From > request.To || (request.To.Value - request.From.Value).TotalDays > 90)
                return InputRules.Problem("from", "Supply explicit from/to bounds with from <= to spanning at most 90 days.");
            var parsed = interval switch { "hour" => TrendInterval.Hour, "week" => TrendInterval.Week, _ => TrendInterval.Day };
            return Results.Ok(await analytics.MultiTrendAsync(projectId, names, request.From.Value.ToUniversalTime(), request.To.Value.ToUniversalTime(), parsed, ct));
        });
    }
}
