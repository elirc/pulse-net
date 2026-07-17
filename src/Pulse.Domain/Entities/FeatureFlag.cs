namespace Pulse.Domain.Entities;

public enum FeatureFlagType
{
    /// <summary>Evaluates to true/false.</summary>
    Boolean,

    /// <summary>Evaluates to one of several variant keys (A/B/n tests).</summary>
    Multivariate,
}

/// <summary>
/// A feature flag. Rollout is a deterministic hash on distinct_id so the same
/// user always gets the same answer; targeting narrows eligibility with
/// person-property and cohort conditions stored as filters JSON.
/// </summary>
public class FeatureFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    /// <summary>Stable key SDKs ask for, e.g. <c>new-onboarding</c>. Unique per project.</summary>
    public required string Key { get; set; }

    public string Name { get; set; } = string.Empty;

    public FeatureFlagType Type { get; set; }

    /// <summary>Inactive flags evaluate to false for everyone.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Percentage of eligible users the flag is on for (0-100).</summary>
    public double RolloutPercentage { get; set; } = 100;

    /// <summary>Targeting conditions: person-property/cohort filters as a JSON array.</summary>
    public string FiltersJson { get; set; } = "[]";

    /// <summary>Multivariate variants as JSON: <c>[{"key":"control","rolloutPercentage":50},…]</c>.</summary>
    public string VariantsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
