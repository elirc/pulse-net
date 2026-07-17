using System.Text.Json;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record InsightRunResult(bool Ok, object? Result, string? Error)
{
    public static InsightRunResult Success(object result) => new(true, result, null);

    public static InsightRunResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Executes a saved insight's stored config through the real query engine —
/// the workhorse behind dashboard refresh. Config parsing mirrors the ad-hoc
/// query endpoints (same defaults, same filter JSON) and degrades to a
/// per-insight error instead of failing the whole refresh.
/// </summary>
public class InsightRunnerService
{
    private readonly QueryService _queries;
    private readonly TimeProvider _clock;

    public InsightRunnerService(QueryService queries, TimeProvider clock)
    {
        _queries = queries;
        _clock = clock;
    }

    public async Task<InsightRunResult> RunAsync(Insight insight, CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(insight.ConfigJson) ? "{}" : insight.ConfigJson);
        }
        catch (JsonException)
        {
            return InsightRunResult.Failure("Insight config is not valid JSON.");
        }

        using (doc)
        {
            var config = doc.RootElement;
            if (config.ValueKind != JsonValueKind.Object)
            {
                return InsightRunResult.Failure("Insight config must be a JSON object.");
            }

            var filtersJson = config.TryGetProperty("filters", out var f)
                              && f.ValueKind == JsonValueKind.Array
                ? f.GetRawText()
                : null;

            if (!PropertyFilterParser.TryParse(filtersJson, out var filters, out var filterError))
            {
                return InsightRunResult.Failure(filterError);
            }

            return insight.Type switch
            {
                InsightType.Trend => await RunTrendAsync(insight.ProjectId, config, filters, ct),
                InsightType.Funnel => await RunFunnelAsync(insight.ProjectId, config, filters, ct),
                InsightType.Retention => await RunRetentionAsync(insight.ProjectId, config, filters, ct),
                _ => InsightRunResult.Failure($"Unknown insight type '{insight.Type}'."),
            };
        }
    }

    private async Task<InsightRunResult> RunTrendAsync(
        Guid projectId,
        JsonElement config,
        List<PropertyFilter> filters,
        CancellationToken ct)
    {
        var eventName = GetString(config, "event");
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return InsightRunResult.Failure("Trend config needs an 'event'.");
        }

        if (!TryGetInterval(config, out var interval))
        {
            return InsightRunResult.Failure("Trend interval must be one of: hour, day, week.");
        }

        var (from, to) = GetRange(config);

        var breakdown = GetString(config, "breakdown");
        if (string.IsNullOrWhiteSpace(breakdown))
        {
            return InsightRunResult.Success(await _queries.TrendAsync(
                projectId, eventName.Trim(), from, to, interval, filters, ct));
        }

        var limit = GetInt(config, "breakdownLimit") ?? 5;
        if (limit is < 1 or > 25)
        {
            return InsightRunResult.Failure("breakdownLimit must be between 1 and 25.");
        }

        return InsightRunResult.Success(await _queries.TrendBreakdownAsync(
            projectId, eventName.Trim(), from, to, interval, breakdown.Trim(), limit, filters, ct));
    }

    private async Task<InsightRunResult> RunFunnelAsync(
        Guid projectId,
        JsonElement config,
        List<PropertyFilter> filters,
        CancellationToken ct)
    {
        var steps = new List<string>();
        if (config.TryGetProperty("steps", out var stepsElement)
            && stepsElement.ValueKind == JsonValueKind.Array)
        {
            steps = stepsElement.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(s.GetString()))
                .Select(s => s.GetString()!.Trim())
                .ToList();
        }

        if (steps.Count < 2)
        {
            return InsightRunResult.Failure("Funnel config needs at least two 'steps'.");
        }

        var windowDays = GetInt(config, "windowDays") ?? 14;
        if (windowDays is < 1 or > 90)
        {
            return InsightRunResult.Failure("windowDays must be between 1 and 90.");
        }

        var (from, to) = GetRange(config);

        return InsightRunResult.Success(await _queries.FunnelAsync(
            projectId, steps, from, to, windowDays, filters, ct));
    }

    private async Task<InsightRunResult> RunRetentionAsync(
        Guid projectId,
        JsonElement config,
        List<PropertyFilter> filters,
        CancellationToken ct)
    {
        var days = GetInt(config, "days") ?? 7;
        if (days is < 1 or > 60)
        {
            return InsightRunResult.Failure("days must be between 1 and 60.");
        }

        var from = GetString(config, "from") is { } raw && DateOnly.TryParse(raw, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-(days - 1));

        var targetEvent = GetString(config, "targetEvent");

        return InsightRunResult.Success(await _queries.RetentionAsync(
            projectId, from, days,
            string.IsNullOrWhiteSpace(targetEvent) ? null : targetEvent.Trim(),
            filters, ct));
    }

    private (DateTimeOffset From, DateTimeOffset To) GetRange(JsonElement config)
    {
        var to = GetString(config, "to") is { } rawTo && DateTimeOffset.TryParse(rawTo, out var parsedTo)
            ? parsedTo
            : _clock.GetUtcNow();

        var from = GetString(config, "from") is { } rawFrom && DateTimeOffset.TryParse(rawFrom, out var parsedFrom)
            ? parsedFrom
            : to.AddDays(-30);

        return (from, to);
    }

    private static bool TryGetInterval(JsonElement config, out TrendInterval interval)
    {
        var raw = GetString(config, "interval");
        if (string.IsNullOrWhiteSpace(raw))
        {
            interval = TrendInterval.Day;
            return true;
        }

        return Enum.TryParse(raw, ignoreCase: true, out interval) && Enum.IsDefined(interval);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
