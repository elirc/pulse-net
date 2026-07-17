using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

/// <summary>
/// Resolves cohort membership. Static cohorts read their member rows;
/// dynamic cohorts evaluate their stored rules (person-property filters and
/// "performed X in the last N days" behavior) against live data.
/// </summary>
public class CohortService
{
    private readonly PulseDbContext _db;
    private readonly TimeProvider _clock;

    public CohortService(PulseDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The person ids currently in the cohort. Unknown or cross-project
    /// cohort ids resolve to the empty set.
    /// </summary>
    public async Task<HashSet<Guid>> GetMemberIdsAsync(
        Guid projectId,
        Guid cohortId,
        CancellationToken ct = default)
    {
        var cohort = await _db.Cohorts
            .SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);

        if (cohort is null)
        {
            return [];
        }

        return cohort.Type == CohortType.Static
            ? await GetStaticMembersAsync(cohort, ct)
            : await EvaluateDynamicAsync(cohort, ct);
    }

    private async Task<HashSet<Guid>> GetStaticMembersAsync(Cohort cohort, CancellationToken ct)
    {
        var ids = await _db.CohortPersons
            .Where(cp => cp.CohortId == cohort.Id)
            .Select(cp => cp.PersonId)
            .ToListAsync(ct);

        return [.. ids];
    }

    private async Task<HashSet<Guid>> EvaluateDynamicAsync(Cohort cohort, CancellationToken ct)
    {
        if (!CohortRuleParser.TryParse(cohort.RulesJson, out var rules, out _) || rules.Count == 0)
        {
            return [];
        }

        HashSet<Guid>? members = null;

        foreach (var rule in rules)
        {
            var ruleMembers = rule.Kind == CohortRuleKind.Property
                ? await EvaluatePropertyRuleAsync(cohort.ProjectId, rule, ct)
                : await EvaluateBehaviorRuleAsync(cohort.ProjectId, rule, ct);

            if (members is null)
            {
                members = ruleMembers;
            }
            else
            {
                members.IntersectWith(ruleMembers); // Rules AND together.
            }

            if (members.Count == 0)
            {
                break;
            }
        }

        return members ?? [];
    }

    private async Task<HashSet<Guid>> EvaluatePropertyRuleAsync(
        Guid projectId,
        CohortRule rule,
        CancellationToken ct)
    {
        var persons = await _db.Persons
            .Where(p => p.ProjectId == projectId)
            .Select(p => new { p.Id, p.PropertiesJson })
            .ToListAsync(ct);

        return persons
            .Where(p => PropertyFilterEvaluator.MatchesSingle(p.PropertiesJson, rule.Filter!))
            .Select(p => p.Id)
            .ToHashSet();
    }

    private async Task<HashSet<Guid>> EvaluateBehaviorRuleAsync(
        Guid projectId,
        CohortRule rule,
        CancellationToken ct)
    {
        var since = _clock.GetUtcNow().AddDays(-rule.Days);

        var matches = await _db.Events
            .Where(e => e.ProjectId == projectId
                        && e.Name == rule.Event
                        && e.Timestamp >= since
                        && e.PersonId != null)
            .GroupBy(e => e.PersonId)
            .Where(g => g.Count() >= rule.MinCount)
            .Select(g => g.Key!.Value)
            .ToListAsync(ct);

        return [.. matches];
    }
}
