using System.Text.Json;
using Pulse.Domain;

namespace Pulse.Api.Contracts;

/// <summary>
/// Wire shape of one property filter:
/// <c>{ "property": "url", "operator": "contains", "value": "/docs", "type": "event" }</c>.
/// <c>type</c> is <c>event</c> (default) or <c>person</c>; operators are
/// <c>equals</c>, <c>contains</c>, <c>is_set</c>, <c>is_not_set</c>.
/// </summary>
public record PropertyFilterDto(
    string? Property,
    string? Operator,
    JsonElement? Value,
    string? Type);

/// <summary>Parses filter payloads (query-string JSON or request-body DTOs) into domain filters.</summary>
public static class FilterParsing
{
    /// <summary>Parses the <c>filters</c> query parameter — a JSON array of filter objects.</summary>
    public static bool TryParseJson(string? json, out List<PropertyFilter> filters, out string error)
    {
        filters = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        List<PropertyFilterDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<PropertyFilterDto>>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            error = "filters must be a JSON array of filter objects.";
            return false;
        }

        return TryConvert(dtos, out filters, out error);
    }

    public static bool TryConvert(
        IReadOnlyList<PropertyFilterDto>? dtos,
        out List<PropertyFilter> filters,
        out string error)
    {
        filters = [];
        error = string.Empty;

        if (dtos is null)
        {
            return true;
        }

        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Property))
            {
                error = "Every filter needs a 'property'.";
                return false;
            }

            FilterOperator? op = dto.Operator?.ToLowerInvariant() switch
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

            FilterTarget? target = (dto.Type ?? "event").ToLowerInvariant() switch
            {
                "event" => FilterTarget.Event,
                "person" => FilterTarget.Person,
                _ => null,
            };

            if (target is null)
            {
                error = "Filter type must be 'event' or 'person'.";
                return false;
            }

            var value = Normalize(dto.Value);
            if (value is null && op is FilterOperator.Equals or FilterOperator.Contains)
            {
                error = $"The '{dto.Operator}' operator requires a 'value'.";
                return false;
            }

            filters.Add(new PropertyFilter(target.Value, dto.Property.Trim(), op.Value, value));
        }

        return true;
    }

    private static string? Normalize(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } element => element.GetString(),
        { } element => element.GetRawText(),
    };
}
