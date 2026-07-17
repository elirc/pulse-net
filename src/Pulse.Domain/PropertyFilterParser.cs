using System.Text.Json;

namespace Pulse.Domain;

/// <summary>
/// Parses a JSON array of filter objects
/// (<c>[{"property":"plan","operator":"equals","value":"pro","type":"person"}]</c>,
/// cohort filters as <c>{"type":"cohort","value":"&lt;id&gt;"}</c>) into domain
/// filters. Used wherever filters are stored or passed as raw JSON.
/// </summary>
public static class PropertyFilterParser
{
    public static bool TryParse(
        string? json,
        out List<PropertyFilter> filters,
        out string error,
        bool allowEventTarget = true)
    {
        filters = [];
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
            error = "Filters must be a JSON array of filter objects.";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Filters must be a JSON array of filter objects.";
                return false;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!TryParseFilter(element, allowEventTarget, out var filter, out error))
                {
                    return false;
                }

                filters.Add(filter!);
            }
        }

        return true;
    }

    private static bool TryParseFilter(
        JsonElement element,
        bool allowEventTarget,
        out PropertyFilter? filter,
        out string error)
    {
        filter = null;
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "Each filter must be a JSON object.";
            return false;
        }

        var type = GetString(element, "type")?.ToLowerInvariant() ?? "event";

        if (type == "cohort")
        {
            var cohortValue = GetValueString(element);
            if (!Guid.TryParse(cohortValue, out _))
            {
                error = "Cohort filters need a 'value' containing the cohort id (GUID).";
                return false;
            }

            filter = new PropertyFilter(FilterTarget.Cohort, "id", FilterOperator.Equals, cohortValue);
            return true;
        }

        FilterTarget? target = type switch
        {
            "event" when allowEventTarget => FilterTarget.Event,
            "person" => FilterTarget.Person,
            _ => null,
        };

        if (target is null)
        {
            error = allowEventTarget
                ? "Filter type must be 'event', 'person' or 'cohort'."
                : "Filter type must be 'person' or 'cohort'.";
            return false;
        }

        var property = GetString(element, "property");
        if (string.IsNullOrWhiteSpace(property))
        {
            error = "Every filter needs a 'property'.";
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
            error = "Filter operator must be one of: equals, contains, is_set, is_not_set.";
            return false;
        }

        var value = GetValueString(element);
        if (value is null && op is FilterOperator.Equals or FilterOperator.Contains)
        {
            error = $"The '{GetString(element, "operator")}' operator requires a 'value'.";
            return false;
        }

        filter = new PropertyFilter(target.Value, property.Trim(), op.Value, value);
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
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
