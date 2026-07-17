using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>
/// Server-side flag evaluation for <c>POST /decide</c>. For each active flag:
/// targeting filters gate eligibility (person properties and cohort
/// membership of the distinct id's person), then the deterministic rollout
/// hash decides on/off, then multivariate flags pick a variant.
/// </summary>
public class FeatureFlagService
{
    private readonly PulseDbContext _db;
    private readonly CohortService _cohorts;

    public FeatureFlagService(PulseDbContext db, CohortService cohorts)
    {
        _db = db;
        _cohorts = cohorts;
    }

    /// <summary>
    /// Evaluates every flag in the project for one distinct id. Values are
    /// <c>bool</c> for boolean flags and the variant key (<c>string</c>) for
    /// multivariate flags that are on; off is always <c>false</c>.
    /// </summary>
    public async Task<Dictionary<string, object>> EvaluateAllAsync(
        Guid projectId,
        string distinctId,
        CancellationToken ct = default)
    {
        var flags = await _db.FeatureFlags
            .Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.Key)
            .ToListAsync(ct);

        var personId = await _db.PersonDistinctIds
            .Where(m => m.ProjectId == projectId && m.DistinctId == distinctId)
            .Select(m => (Guid?)m.PersonId)
            .SingleOrDefaultAsync(ct);

        string? personProps = null;
        if (personId is not null)
        {
            personProps = await _db.Persons
                .Where(p => p.Id == personId)
                .Select(p => p.PropertiesJson)
                .SingleOrDefaultAsync(ct);
        }

        var results = new Dictionary<string, object>(flags.Count);
        foreach (var flag in flags)
        {
            results[flag.Key] = await EvaluateAsync(flag, distinctId, personId, personProps, ct);
        }

        return results;
    }

    private async Task<object> EvaluateAsync(
        FeatureFlag flag,
        string distinctId,
        Guid? personId,
        string? personProps,
        CancellationToken ct)
    {
        if (!flag.Active)
        {
            return false;
        }

        if (!await MatchesTargetingAsync(flag, personId, personProps, ct))
        {
            return false;
        }

        if (!FeatureFlagHasher.IsInRollout(flag.Key, distinctId, flag.RolloutPercentage))
        {
            return false;
        }

        if (flag.Type == FeatureFlagType.Boolean)
        {
            return true;
        }

        if (!FlagVariantParser.TryParse(flag.VariantsJson, out var variants, out _)
            || variants.Count == 0)
        {
            return true; // Multivariate without variants degrades to boolean-on.
        }

        return FeatureFlagHasher.PickVariant(
            flag.Key,
            distinctId,
            variants.Select(v => (v.Key, v.RolloutPercentage)).ToList());
    }

    private async Task<bool> MatchesTargetingAsync(
        FeatureFlag flag,
        Guid? personId,
        string? personProps,
        CancellationToken ct)
    {
        if (!PropertyFilterParser.TryParse(
                flag.FiltersJson, out var filters, out _, allowEventTarget: false)
            || filters.Count == 0)
        {
            return true; // No targeting: everyone is eligible.
        }

        foreach (var filter in filters)
        {
            if (filter.Target == FilterTarget.Cohort)
            {
                if (personId is null)
                {
                    return false;
                }

                var cohortId = Guid.TryParse(filter.Value, out var parsed) ? parsed : Guid.Empty;
                var members = await _cohorts.GetMemberIdsAsync(flag.ProjectId, cohortId, ct);
                if (!members.Contains(personId.Value))
                {
                    return false;
                }

                continue;
            }

            if (!PropertyFilterEvaluator.MatchesSingle(personProps, filter))
            {
                return false;
            }
        }

        return true;
    }
}
