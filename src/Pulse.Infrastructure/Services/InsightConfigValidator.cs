using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record ValidatedInsightConfig(InsightType Type, string StorageJson, string RunJson,
    IReadOnlyList<PropertyFilter> Filters, Dictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Strict validation for preview/replacement/templates; legacy saved creation retains its contract.</summary>
public static class InsightConfigValidator
{
    public const int MaximumConfigBytes = 32 * 1024;

    public static ValidatedInsightConfig Validate(string? rawType, JsonElement? input, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        void Error(string key, string message) => errors["config." + key] = [message];
        var typeName = rawType?.Trim().ToLowerInvariant();
        var type = typeName switch { "trend" => InsightType.Trend, "funnel" => InsightType.Funnel, "retention" => InsightType.Retention, _ => (InsightType?)null };
        if (type is null) errors["type"] = ["Type must be trend, funnel, or retention."];
        if (input is not { ValueKind: JsonValueKind.Object } config)
        {
            errors["config"] = ["Config must be an object."];
            return new(type ?? default, "{}", "{}", [], errors);
        }
        if (Encoding.UTF8.GetByteCount(config.GetRawText()) > MaximumConfigBytes)
        {
            errors["config"] = ["Config must not exceed 32 KiB of UTF-8 JSON."];
            return new(type ?? default, "{}", "{}", [], errors);
        }
        // JsonObject requires unique property names. Reject ambiguity deliberately.
        if (config.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() > 1))
        {
            errors["config"] = ["Config property names must be unique."];
            return new(type ?? default, "{}", "{}", [], errors);
        }
        var storage = JsonNode.Parse(config.GetRawText())!.AsObject();
        var filters = new List<PropertyFilter>();
        if (config.TryGetProperty("filters", out var filterInput))
        {
            if (!PropertyFilterParser.TryParse(filterInput.GetRawText(), out filters, out var message)) Error("filters", message);
        }

        string? Text(string key, bool required, int maximum = 200)
        {
            if (!config.TryGetProperty(key, out var value))
            {
                if (required) Error(key, "A nonblank string is required.");
                return null;
            }
            if (value.ValueKind != JsonValueKind.String) { Error(key, "Must be a string."); return null; }
            var text = value.GetString()!.Trim();
            if (text.Length == 0 || text.Length > maximum) { Error(key, $"Use 1–{maximum} characters after trimming."); return null; }
            storage[key] = text;
            return text;
        }

        int Integer(string key, int fallback, int minimum, int maximum)
        {
            if (!config.TryGetProperty(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum)
            {
                Error(key, $"Must be an integer between {minimum} and {maximum}.");
                return fallback;
            }
            storage[key] = parsed;
            return parsed;
        }

        DateTimeOffset Bound(string key, DateTimeOffset fallback)
        {
            if (!config.TryGetProperty(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                Error(key, "Must be a valid timestamp.");
                return fallback;
            }
            storage[key] = date.ToString("O", CultureInfo.InvariantCulture);
            return date;
        }

        DateTimeOffset? from = null, to = null;
        DateOnly? retentionFrom = null;
        if (type is InsightType.Trend or InsightType.Funnel)
        {
            to = Bound("to", now);
            DateTimeOffset defaultFrom;
            try { defaultFrom = to.Value.AddDays(-30); }
            catch (ArgumentOutOfRangeException) { defaultFrom = to.Value; if (!config.TryGetProperty("from", out _)) Error("from", "Default range precedes the supported date range; supply from."); }
            from = Bound("from", defaultFrom);
            if (from > to) Error("from", "from must not be after to.");
            else if ((to.Value - from.Value).TotalDays > 90) Error("from", "Range must not exceed 90 days.");
        }
        if (type == InsightType.Trend)
        {
            Text("event", required: true);
            var interval = Text("interval", required: false)?.ToLowerInvariant();
            if (interval is not null)
            {
                if (interval is not ("hour" or "day" or "week")) Error("interval", "Interval must be hour, day, or week.");
                else storage["interval"] = interval;
            }
            Text("breakdown", required: false);
            Integer("breakdownLimit", 5, 1, 25);
        }
        if (type == InsightType.Funnel)
        {
            Integer("windowDays", 14, 1, 90);
            if (!config.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 2 or > 20)
                Error("steps", "Supply between 2 and 20 event names.");
            else
            {
                var names = new JsonArray();
                var index = 0;
                foreach (var step in steps.EnumerateArray())
                {
                    var name = step.ValueKind == JsonValueKind.String ? step.GetString()!.Trim() : null;
                    if (name is null || name.Length is < 1 or > 200) Error($"steps[{index}]", "Each event must be a string of 1–200 trimmed characters.");
                    else names.Add(name);
                    index++;
                }
                storage["steps"] = names;
            }
        }
        if (type == InsightType.Retention)
        {
            var days = Integer("days", 7, 1, 60);
            retentionFrom = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(days - 1));
            if (config.TryGetProperty("from", out var value))
            {
                if (value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    Error("from", "Use a valid YYYY-MM-DD date.");
                else { retentionFrom = parsed; storage["from"] = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            }
            if (retentionFrom.Value.DayNumber + days > DateOnly.MaxValue.DayNumber) Error("from", "Retention window exceeds the supported date range.");
            Text("targetEvent", required: false);
        }
        var run = (JsonObject)storage.DeepClone();
        if (from is { } start && to is { } end)
        {
            run["from"] = start.ToString("O", CultureInfo.InvariantCulture);
            run["to"] = end.ToString("O", CultureInfo.InvariantCulture);
        }
        if (retentionFrom is { } day) run["from"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new(type ?? default, storage.ToJsonString(), run.ToJsonString(), filters, errors);
    }
}
