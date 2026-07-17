using System.Text.Json;

namespace Pulse.Domain;

/// <summary>What a filter inspects: the event's own properties, the properties of the person who performed it, or cohort membership.</summary>
public enum FilterTarget
{
    Event,
    Person,

    /// <summary>
    /// Restricts events to persons in a cohort. <see cref="PropertyFilter.Value"/>
    /// holds the cohort id; membership is resolved by the query engine, not
    /// the JSON evaluator.
    /// </summary>
    Cohort,
}

public enum FilterOperator
{
    Equals,
    Contains,
    IsSet,
    IsNotSet,
}

/// <summary>One predicate over a JSON properties document. Filters combine with AND.</summary>
public record PropertyFilter(
    FilterTarget Target,
    string Property,
    FilterOperator Operator,
    string? Value);

/// <summary>
/// Evaluates <see cref="PropertyFilter"/>s against JSON property documents.
/// Values are compared through a canonical string form so
/// <c>{"plan": "pro"}</c> and <c>{"count": 42}</c> can both be filtered with
/// string-typed filter values.
/// </summary>
public static class PropertyFilterEvaluator
{
    /// <summary>True when every filter matches (AND semantics). An empty list matches.</summary>
    public static bool Matches(string? propertiesJson, IEnumerable<PropertyFilter> filters) =>
        filters.All(f => MatchesSingle(propertiesJson, f));

    public static bool MatchesSingle(string? propertiesJson, PropertyFilter filter)
    {
        var value = GetValue(propertiesJson, filter.Property);

        return filter.Operator switch
        {
            FilterOperator.IsSet => value is not null,
            FilterOperator.IsNotSet => value is null,
            FilterOperator.Equals => value is not null
                && string.Equals(value, filter.Value, StringComparison.Ordinal),
            FilterOperator.Contains => value is not null
                && filter.Value is not null
                && value.Contains(filter.Value, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// The canonical string form of a top-level property, or null when the
    /// document is empty/invalid, the property is absent, or its value is
    /// JSON null. Strings are unquoted; other values use their raw JSON text.
    /// </summary>
    public static string? GetValue(string? propertiesJson, string property)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(propertiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(property, out var element))
            {
                return null;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                _ => element.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
