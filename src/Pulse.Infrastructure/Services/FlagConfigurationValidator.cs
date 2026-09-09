using System.Text.Json;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>Shared configuration semantics for creation, editing, cloning, and future restoration.</summary>
public static class FlagConfigurationValidator
{
    public static bool TryValidate(double? rolloutPercentage, JsonElement? filters, JsonElement? variants, FeatureFlagType type,
        Dictionary<string, string[]> errors, out double rollout, out string filtersJson, out string variantsJson)
    {
        rollout = rolloutPercentage ?? 100;
        if (!double.IsFinite(rollout) || rollout is < 0 or > 100)
            errors["rolloutPercentage"] = ["rolloutPercentage must be between 0 and 100."];
        filtersJson = filters is { ValueKind: JsonValueKind.Array } f ? f.GetRawText() : "[]";
        if (!PropertyFilterParser.TryParse(filtersJson, out _, out var filterError, allowEventTarget: false)) errors["filters"] = [filterError];
        variantsJson = variants is { ValueKind: JsonValueKind.Array } v ? v.GetRawText() : "[]";
        if (type == FeatureFlagType.Multivariate)
        {
            if (!FlagVariantParser.TryParse(variantsJson, out _, out var variantError)) errors["variants"] = [variantError];
        }
        else if (variantsJson != "[]") errors["variants"] = ["Boolean flags cannot have variants."];
        return errors.Count == 0;
    }

    public static bool IsValidStored(FeatureFlag source)
    {
        if (!Enum.IsDefined(source.Type)) return false;
        try
        {
            var filters = JsonSerializer.Deserialize<JsonElement>(source.FiltersJson);
            var variants = JsonSerializer.Deserialize<JsonElement>(source.VariantsJson);
            if (filters.ValueKind != JsonValueKind.Array || variants.ValueKind != JsonValueKind.Array) return false;
            if (source.Type == FeatureFlagType.Multivariate && variants.GetArrayLength() == 0) return false;
            return TryValidate(source.RolloutPercentage, filters, variants, source.Type, new(), out _, out _, out _);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return false; }
    }
}
