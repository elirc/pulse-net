using System.Text.Json;

namespace Pulse.Domain;

public enum CohortRuleKind
{
    /// <summary>Matches persons whose properties satisfy a filter.</summary>
    Property,

    /// <summary>Matches persons who performed an event in the last N days.</summary>
    PerformedEvent,
}

/// <summary>
/// One dynamic-cohort rule; rules combine with AND. Property rules carry a
/// person-targeted <see cref="PropertyFilter"/>; behavioral rules carry the
/// event name, lookback window and minimum count.
/// </summary>
public record CohortRule(
    CohortRuleKind Kind,
    PropertyFilter? Filter,
    string? Event,
    int Days,
    int MinCount);

/// <summary>
/// Parses the stored rules JSON, e.g.
/// <c>[{"kind":"property","property":"plan","operator":"equals","value":"pro"},
/// {"kind":"performed_event","event":"purchase","days":30,"minCount":2}]</c>.
/// </summary>
public static class CohortRuleParser
{
    public static bool TryParse(string? json, out List<CohortRule> rules, out string error)
    {
        rules = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "Rules must be a JSON array of rule objects.";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Rules must be a JSON array of rule objects.";
                return false;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!TryParseRule(element, out var rule, out error))
                {
                    return false;
                }

                rules.Add(rule!);
            }
        }

        return true;
    }

    private static bool TryParseRule(JsonElement element, out CohortRule? rule, out string error)
    {
        rule = null;
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each rule must be a JSON object.";
            return false;
        }

        var kind = GetString(element, "kind")?.ToLowerInvariant();
        switch (kind)
        {
            case "property":
            {
                var property = GetString(element, "property");
                if (string.IsNullOrWhiteSpace(property))
                {
                    error = "Property rules need a 'property'.";
                    return false;
                }

                FilterOperator? op = GetString(element, "operator")?.ToLowerInvariant() switch
                {
                    "equals" => FilterOperator.Equals,
                    "contains" => FilterOperator.Contains,
                    "is_set" => FilterOperator.IsSet,
                    "is_not_set" => FilterOperator.IsNotSet,
                    _ => null,
                };

                if (op is null)
                {
                    error = "Property rule operator must be one of: equals, contains, is_set, is_not_set.";
                    return false;
                }

                var value = GetValueString(element);
                if (value is null && op is FilterOperator.Equals or FilterOperator.Contains)
                {
                    error = "Property rules with equals/contains need a 'value'.";
                    return false;
                }

                rule = new CohortRule(
                    CohortRuleKind.Property,
                    new PropertyFilter(FilterTarget.Person, property.Trim(), op.Value, value),
                    Event: null,
                    Days: 0,
                    MinCount: 0);
                return true;
            }

            case "performed_event":
            {
                var eventName = GetString(element, "event");
                if (string.IsNullOrWhiteSpace(eventName))
                {
                    error = "performed_event rules need an 'event'.";
                    return false;
                }

                var days = GetInt(element, "days") ?? 30;
                if (days is < 1 or > 365)
                {
                    error = "performed_event 'days' must be between 1 and 365.";
                    return false;
                }

                var minCount = GetInt(element, "minCount") ?? 1;
                if (minCount < 1)
                {
                    error = "performed_event 'minCount' must be at least 1.";
                    return false;
                }

                rule = new CohortRule(
                    CohortRuleKind.PerformedEvent,
                    Filter: null,
                    Event: eventName.Trim(),
                    Days: days,
                    MinCount: minCount);
                return true;
            }

            default:
                error = "Each rule needs a 'kind' of 'property' or 'performed_event'.";
                return false;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string? GetValueString(JsonElement element)
    {
        if (!element.TryGetProperty("value", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText(),
        };
    }
}
