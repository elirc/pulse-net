using System.Text.Json;

namespace Pulse.Domain;

public record FlagVariant(string Key, double RolloutPercentage);

/// <summary>
/// Parses multivariate variants JSON:
/// <c>[{"key":"control","rolloutPercentage":50},{"key":"test","rolloutPercentage":50}]</c>.
/// Percentages must sum to 100.
/// </summary>
public static class FlagVariantParser
{
    public static bool TryParse(string? json, out List<FlagVariant> variants, out string error)
    {
        variants = [];
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
            error = "Variants must be a JSON array.";
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "Variants must be a JSON array.";
                return false;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("key", out var key)
                    || key.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(key.GetString()))
                {
                    error = "Every variant needs a non-empty 'key'.";
                    return false;
                }

                if (!element.TryGetProperty("rolloutPercentage", out var rollout)
                    || rollout.ValueKind != JsonValueKind.Number)
                {
                    error = "Every variant needs a numeric 'rolloutPercentage'.";
                    return false;
                }

                var percentage = rollout.GetDouble();
                if (percentage is < 0 or > 100)
                {
                    error = "Variant rolloutPercentage must be between 0 and 100.";
                    return false;
                }

                variants.Add(new FlagVariant(key.GetString()!.Trim(), percentage));
            }
        }

        if (variants.Count == 0)
        {
            return true;
        }

        if (variants.Select(v => v.Key).Distinct(StringComparer.Ordinal).Count() != variants.Count)
        {
            error = "Variant keys must be unique.";
            variants = [];
            return false;
        }

        if (Math.Abs(variants.Sum(v => v.RolloutPercentage) - 100) > 0.001)
        {
            error = "Variant rollout percentages must sum to 100.";
            variants = [];
            return false;
        }

        return true;
    }
}
