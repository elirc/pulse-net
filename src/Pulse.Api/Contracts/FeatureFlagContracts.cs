using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Api.Contracts;

/// <summary>
/// <c>type</c> is <c>boolean</c> or <c>multivariate</c>. <c>filters</c> is a
/// JSON array of person/cohort targeting conditions; <c>variants</c> is a
/// JSON array of <c>{"key","rolloutPercentage"}</c> summing to 100.
/// </summary>
public record CreateFeatureFlagRequest(
    string? Key,
    string? Name,
    string? Type,
    bool? Active,
    double? RolloutPercentage,
    JsonElement? Filters,
    JsonElement? Variants);

public record UpdateFeatureFlagRequest(
    string? Name,
    bool? Active,
    double? RolloutPercentage,
    JsonElement? Filters,
    JsonElement? Variants);

public record FeatureFlagResponse(
    Guid Id,
    Guid ProjectId,
    string Key,
    string Name,
    string Type,
    bool Active,
    double RolloutPercentage,
    JsonElement Filters,
    JsonElement Variants,
    DateTimeOffset CreatedAt);

/// <summary>Everything an SDK needs to evaluate flags locally without calling /decide per user.</summary>
public record LocalEvaluationResponse(IReadOnlyList<FeatureFlagResponse> Flags);

public record DecideRequest
{
    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("distinct_id")]
    public string? DistinctId { get; init; }
}

public record DecideResponse(Dictionary<string, object> FeatureFlags);
