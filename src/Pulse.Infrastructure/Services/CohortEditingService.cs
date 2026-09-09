using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public static class CohortCandidateRules
{
    public static bool TryValidate(JsonElement? input, out string json, out List<CohortRule> rules, out string error)
    {
        json = "[]"; rules = [];
        error = "Supply between 1 and 10 rules, with at most 32 KiB of rules JSON.";
        if (input is not { ValueKind: JsonValueKind.Array } value || value.GetArrayLength() is < 1 or > 10) return false;
        json = value.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024) return false;
        // Existing parser semantics remain authoritative, including omitted numeric defaults.
        try { return CohortRuleParser.TryParse(json, out rules, out error); }
        catch (InvalidOperationException) { error = "Rule fields have invalid JSON types."; return false; }
    }
}

public record CohortPreview(DateTimeOffset EvaluatedAt, int Count, IReadOnlyList<Guid> SamplePersonIds);
public record CohortSnapshotOutcome(int Status, Cohort? Cohort = null, int MemberCount = 0, DateTimeOffset? EvaluatedAt = null, string? Detail = null);
public record CohortReplacementOutcome(int Status, int Added = 0, int Removed = 0, int Unchanged = 0, int Total = 0);

public sealed class CohortEditingService(PulseDbContext db, CohortService cohorts, TimeProvider clock)
{
    public async Task<CohortPreview> PreviewAsync(Guid projectId, IReadOnlyList<CohortRule> rules, int limit, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var members = await cohorts.EvaluateRulesAsync(projectId, rules, now, ct);
        return new(now, members.Count, members.Order().Take(limit).ToList());
    }

    public async Task<CohortSnapshotOutcome> SnapshotAsync(Guid projectId, Guid cohortId, string name, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        var source = await db.Cohorts.SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);
        if (source is null) return new(404);
        if (source.Type != CohortType.Dynamic) return new(409, Detail: "Only dynamic cohorts can be snapshotted.");
        List<CohortRule> rules;
        try
        {
            if (!CohortRuleParser.TryParse(source.RulesJson, out rules, out _) || rules.Count == 0)
                return new(409, Detail: "The source has invalid or empty rules.");
        }
        catch (InvalidOperationException) { return new(409, Detail: "The source has invalid rules."); }
        var now = clock.GetUtcNow();
        var members = await cohorts.EvaluateRulesAsync(projectId, rules, now, ct);
        if (members.Count > 1000) return new(409, Detail: "Snapshots support at most 1,000 matching people.");
        if (await db.Persons.CountAsync(p => p.ProjectId == projectId && members.Contains(p.Id), ct) != members.Count)
            return new(409, Detail: "The evaluated membership contains unavailable people.");
        var copy = new Cohort { ProjectId = projectId, Name = name, Type = CohortType.Static, RulesJson = "[]", CreatedAt = now };
        db.Cohorts.Add(copy);
        db.CohortPersons.AddRange(members.Select(id => new CohortPerson { CohortId = copy.Id, PersonId = id, AddedAt = now }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(201, copy, members.Count, now);
    }

    public async Task<CohortReplacementOutcome> ReplaceMembersAsync(Guid projectId, Guid cohortId, IReadOnlyList<Guid> requested, CancellationToken ct)
    {
        if (requested.Count > 1000) return new(400);
        var desired = requested.ToHashSet();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await ProjectMaintenance.AssertWritableAsync(db, projectId, ct);
        var cohort = await db.Cohorts.SingleOrDefaultAsync(c => c.ProjectId == projectId && c.Id == cohortId, ct);
        if (cohort is null) return new(404);
        if (cohort.Type != CohortType.Static) return new(409);
        if (await db.Persons.CountAsync(p => p.ProjectId == projectId && desired.Contains(p.Id), ct) != desired.Count) return new(400);
        var existing = (await db.CohortPersons.Where(cp => cp.CohortId == cohortId).Select(cp => cp.PersonId).ToListAsync(ct)).ToHashSet();
        var added = desired.Except(existing).ToArray();
        var removed = existing.Except(desired).ToArray();
        await db.CohortPersons.Where(cp => cp.CohortId == cohortId && removed.Contains(cp.PersonId)).ExecuteDeleteAsync(ct);
        var now = clock.GetUtcNow();
        db.CohortPersons.AddRange(added.Select(id => new CohortPerson { CohortId = cohortId, PersonId = id, AddedAt = now }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(200, added.Length, removed.Length, existing.Intersect(desired).Count(), desired.Count);
    }
}
