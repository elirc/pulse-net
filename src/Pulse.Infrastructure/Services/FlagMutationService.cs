using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record FlagMutationOutcome(int Status, FeatureFlag? Flag = null);

public record FlagConfiguration(string Name, string Type, bool Active, double RolloutPercentage, string FiltersJson, string VariantsJson)
{
    public static FlagConfiguration From(FeatureFlag flag) => new(flag.Name, flag.Type.ToString().ToLowerInvariant(), flag.Active,
        flag.RolloutPercentage, flag.FiltersJson, flag.VariantsJson);
    public string Serialize() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);
    public bool Apply(FeatureFlag flag)
    {
        if (!Enum.TryParse<FeatureFlagType>(Type, true, out var type) || !Enum.IsDefined(type) || Name is null || FiltersJson is null || VariantsJson is null) return false;
        flag.Name = Name; flag.Type = type; flag.Active = Active; flag.RolloutPercentage = RolloutPercentage;
        flag.FiltersJson = FiltersJson; flag.VariantsJson = VariantsJson;
        return FlagConfigurationValidator.IsValidStored(flag);
    }
}

/// <summary>Every management flag writer commits configuration, revision, history, and audit together.</summary>
public sealed class FlagMutationService(PulseDbContext db, TimeProvider clock)
{
    public const int MaxConfigurationBytes = 64 * 1024;
    public static bool Fits(FeatureFlag flag) => Encoding.UTF8.GetByteCount(FlagConfiguration.From(flag).Serialize()) <= MaxConfigurationBytes;

    public async Task<int> CheckEditorAsync(Guid projectId, AuditActor actor, CancellationToken ct)
    {
        var role = await db.ProjectMemberships.Where(m => m.ProjectId == projectId && m.UserId == actor.UserId)
            .Select(m => (ProjectRole?)m.Role).SingleOrDefaultAsync(ct);
        return role is null ? 404 : role >= ProjectRole.Editor ? 200 : 403;
    }

    public async Task<FlagMutationOutcome> CreateAsync(FeatureFlag flag, AuditActor actor, CancellationToken ct)
    {
        if (!Fits(flag)) return new(400);
        var permission = await CheckEditorAsync(flag.ProjectId, actor, ct);
        if (permission != 200) return new(permission);
        flag.Revision = 1;
        flag.CreatedAt = clock.GetUtcNow();
        db.FeatureFlags.Add(flag);
        StageEvidence(flag, actor, "created", flag.CreatedAt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (FlagDraftService.IsProjectKeyConflict(error))
        {
            db.ChangeTracker.Clear();
            return new(409);
        }
        return new(201, flag);
    }

    public async Task<FlagMutationOutcome> UpdateAsync(FeatureFlag candidate, long expectedRevision, AuditActor actor,
        CancellationToken ct, long? restoredFrom = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await UpdateInTransactionAsync(candidate, expectedRevision, actor, ct, restoredFrom);
        if (result.Status != 200) return result;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    // The scheduler owns its outer transaction so its terminal state joins this mutation.
    public async Task<FlagMutationOutcome> UpdateInTransactionAsync(FeatureFlag candidate, long expectedRevision,
        AuditActor actor, CancellationToken ct, long? restoredFrom = null, Guid? scheduleId = null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Flag updates require a caller-owned transaction.");
        if (!Fits(candidate)) return new(400);
        // Acquire the same SQLite write gate used by membership edits before checking current authority.
        await db.Projects.Where(p => p.Id == candidate.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name), ct);
        var permission = await CheckEditorAsync(candidate.ProjectId, actor, ct);
        if (permission != 200) return new(permission);
        if (expectedRevision < 1 || expectedRevision == long.MaxValue) return new(412);
        var nextRevision = expectedRevision + 1;
        var changed = await db.FeatureFlags.Where(f => f.ProjectId == candidate.ProjectId && f.Id == candidate.Id && f.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, candidate.Name).SetProperty(f => f.Type, candidate.Type)
                .SetProperty(f => f.Active, candidate.Active).SetProperty(f => f.RolloutPercentage, candidate.RolloutPercentage)
                .SetProperty(f => f.FiltersJson, candidate.FiltersJson).SetProperty(f => f.VariantsJson, candidate.VariantsJson)
                .SetProperty(f => f.Revision, nextRevision), ct);
        if (changed == 0) return new(await db.FeatureFlags.AnyAsync(f => f.ProjectId == candidate.ProjectId && f.Id == candidate.Id, ct) ? 412 : 404);
        candidate.Revision = nextRevision;
        StageEvidence(candidate, actor, restoredFrom is null ? "updated" : "restored", clock.GetUtcNow(), expectedRevision, restoredFrom, scheduleId);
        // Current + previous 99 revisions; bulk deletion participates in the outer transaction.
        await db.FlagVersions.Where(v => v.ProjectId == candidate.ProjectId && v.FlagId == candidate.Id && v.Revision <= nextRevision - 100).ExecuteDeleteAsync(ct);
        return new(200, candidate);
    }

    public async Task<int> DeleteAsync(FeatureFlag flag, long expectedRevision, AuditActor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Projects.Where(p => p.Id == flag.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name), ct);
        var permission = await CheckEditorAsync(flag.ProjectId, actor, ct);
        if (permission != 200) return permission;
        var changed = await db.FeatureFlags.Where(f => f.ProjectId == flag.ProjectId && f.Id == flag.Id && f.Revision == expectedRevision).ExecuteDeleteAsync(ct);
        if (changed == 0) return await db.FeatureFlags.AnyAsync(f => f.ProjectId == flag.ProjectId && f.Id == flag.Id, ct) ? 412 : 404;
        await db.FlagVersions.Where(v => v.ProjectId == flag.ProjectId && v.FlagId == flag.Id).ExecuteDeleteAsync(ct);
        ManagementAuditWriter.Flag(db, flag, actor, "flag.deleted", clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return 204;
    }

    public async Task<FlagMutationOutcome> RestoreAsync(FeatureFlag live, long targetRevision, long expectedRevision, AuditActor actor, CancellationToken ct)
    {
        var version = await db.FlagVersions.AsNoTracking().SingleOrDefaultAsync(v => v.ProjectId == live.ProjectId && v.FlagId == live.Id && v.Revision == targetRevision, ct);
        if (version is null) return new(404);
        try
        {
            var config = JsonSerializer.Deserialize<FlagConfiguration>(version.ConfigJson, JsonSerializerOptions.Web);
            if (config is null || !config.Apply(live) || !Fits(live)) return new(409);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return new(409); }
        if (!PropertyFilterParser.TryParse(live.FiltersJson, out var filters, out _, allowEventTarget: false)) return new(409);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Lock before validating mutable cohort references; a concurrent deletion cannot slip between check and commit.
        await db.Projects.Where(p => p.Id == live.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, p => p.Name), ct);
        foreach (var filter in filters.Where(f => f.Target == FilterTarget.Cohort))
            if (!Guid.TryParse(filter.Value, out var id) || !await db.Cohorts.AnyAsync(c => c.ProjectId == live.ProjectId && c.Id == id, ct)) return new(409);
        var result = await UpdateInTransactionAsync(live, expectedRevision, actor, ct, targetRevision);
        if (result.Status != 200) return result;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private void StageEvidence(FeatureFlag flag, AuditActor actor, string origin, DateTimeOffset now,
        long? previousRevision = null, long? restoredFrom = null, Guid? scheduleId = null)
    {
        db.FlagVersions.Add(new FlagVersion { ProjectId = flag.ProjectId, FlagId = flag.Id, Revision = flag.Revision,
            ConfigJson = FlagConfiguration.From(flag).Serialize(), RecordedAt = now, ActorUserId = actor.UserId,
            PersonalKeyId = actor.PersonalKeyId, Origin = scheduleId is null ? origin : "scheduled", RestoredFromRevision = restoredFrom });
        ManagementAuditWriter.Flag(db, flag, actor, origin == "created" ? "flag.created" : "flag.updated", now, previousRevision, restoredFrom, scheduleId);
    }
}
