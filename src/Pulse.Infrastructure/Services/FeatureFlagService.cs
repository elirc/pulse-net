using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record FlagDecisionStage(string Stage, string Outcome, string Reason);
public record FlagDecision(string Key, object Value, bool PersonResolved, IReadOnlyList<FlagDecisionStage> Stages, IReadOnlyList<string> Warnings);
public record FlagBatchIdentity(string DistinctId, Dictionary<string, object> FeatureFlags);
public record FlagBatchResult(IReadOnlyList<FlagBatchIdentity> Results);

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
        var cohortCache = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var flag in flags)
        {
            results[flag.Key] = (await EvaluateDecisionAsync(flag, distinctId, personId, personProps, cohortCache, ct)).Value;
        }

        return results;
    }

    public async Task<FlagDecision?> ExplainAsync(Guid projectId, string key, string distinctId, CancellationToken ct)
    {
        var flag = await _db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == key, ct);
        if (flag is null) return null;
        var personId = await _db.PersonDistinctIds.Where(m => m.ProjectId == projectId && m.DistinctId == distinctId)
            .Select(m => (Guid?)m.PersonId).SingleOrDefaultAsync(ct);
        var properties = personId is null ? null : await _db.Persons.Where(p => p.ProjectId == projectId && p.Id == personId)
            .Select(p => p.PropertiesJson).SingleOrDefaultAsync(ct);
        return await EvaluateDecisionAsync(flag, distinctId, personId, properties, new(), ct);
    }

    public async Task<FlagBatchResult?> EvaluateBatchAsync(Guid projectId, IReadOnlyList<string> identities, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var flags = await _db.FeatureFlags.AsNoTracking().Where(f => f.ProjectId == projectId && keys.Contains(f.Key)).ToListAsync(ct);
        if (flags.Count != keys.Count) return null;
        var mappings = await _db.PersonDistinctIds.AsNoTracking().Where(m => m.ProjectId == projectId && identities.Contains(m.DistinctId))
            .Select(m => new { m.DistinctId, m.PersonId }).ToDictionaryAsync(m => m.DistinctId, m => m.PersonId, ct);
        var ids = mappings.Values.Distinct().ToArray();
        var people = await _db.Persons.AsNoTracking().Where(p => p.ProjectId == projectId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.PropertiesJson }).ToDictionaryAsync(p => p.Id, p => p.PropertiesJson, ct);
        var byKey = flags.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var cohortCache = new Dictionary<Guid, HashSet<Guid>>();
        var results = new List<FlagBatchIdentity>();
        foreach (var identity in identities)
        {
            Guid? personId = mappings.TryGetValue(identity, out var id) ? id : null;
            var properties = personId is { } person ? people.GetValueOrDefault(person) : null;
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var key in keys)
                values.Add(key, (await EvaluateDecisionAsync(byKey[key], identity, personId, properties, cohortCache, ct)).Value);
            results.Add(new(identity, values));
        }
        return new(results);
    }

    private async Task<FlagDecision> EvaluateDecisionAsync(
        FeatureFlag flag,
        string distinctId,
        Guid? personId,
        string? personProps,
        Dictionary<Guid, HashSet<Guid>> cohortCache,
        CancellationToken ct)
    {
        var stages = new List<FlagDecisionStage>();
        var warnings = new List<string>();
        FlagDecision Finish(object value)
        {
            foreach (var stage in new[] { "active", "targeting", "rollout", "variant" }.Skip(stages.Count))
                stages.Add(new(stage, "skipped", "earlier-gate-failed"));
            return new(flag.Key, value, personId is not null, stages, warnings);
        }
        if (!flag.Active)
        {
            stages.Add(new("active", "failed", "inactive"));
            return Finish(false);
        }
        stages.Add(new("active", "passed", "active"));

        if (!PropertyFilterParser.TryParse(flag.FiltersJson, out var filters, out _, allowEventTarget: false))
        {
            // Preserve the existing unrestricted fallback; expose a stable diagnostic code.
            warnings.Add("invalid-targeting-unrestricted");
            filters = [];
        }
        foreach (var filter in filters)
        {
            if (filter.Target == FilterTarget.Cohort)
            {
                if (personId is null)
                {
                    stages.Add(new("targeting", "failed", "person-unresolved"));
                    return Finish(false);
                }

                var cohortId = Guid.TryParse(filter.Value, out var parsed) ? parsed : Guid.Empty;
                if (!cohortCache.TryGetValue(cohortId, out var members))
                {
                    members = await _cohorts.GetMemberIdsAsync(flag.ProjectId, cohortId, ct);
                    cohortCache.Add(cohortId, members);
                }
                if (!members.Contains(personId.Value))
                {
                    stages.Add(new("targeting", "failed", "cohort-miss"));
                    return Finish(false);
                }

                continue;
            }

            if (!PropertyFilterEvaluator.MatchesSingle(personProps, filter))
            {
                stages.Add(new("targeting", "failed", "property-miss"));
                return Finish(false);
            }
        }
        stages.Add(new("targeting", "passed", filters.Count == 0 ? "unrestricted" : "matched"));
        if (!FeatureFlagHasher.IsInRollout(flag.Key, distinctId, flag.RolloutPercentage))
        {
            stages.Add(new("rollout", "failed", "outside-rollout"));
            return Finish(false);
        }
        stages.Add(new("rollout", "passed", "inside-rollout"));
        if (flag.Type == FeatureFlagType.Boolean)
        {
            stages.Add(new("variant", "skipped", "boolean-flag"));
            return Finish(true);
        }
        if (!FlagVariantParser.TryParse(flag.VariantsJson, out var variants, out _) || variants.Count == 0)
        {
            warnings.Add("invalid-variants-boolean-on");
            stages.Add(new("variant", "skipped", "boolean-fallback"));
            return Finish(true);
        }
        var variant = FeatureFlagHasher.PickVariant(flag.Key, distinctId, variants.Select(v => (v.Key, v.RolloutPercentage)).ToList());
        stages.Add(new("variant", "selected", "deterministic-variant"));
        return Finish(variant);
    }
}
