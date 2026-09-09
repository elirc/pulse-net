using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record FlagDraftOutcome(int Status, FeatureFlag? Flag = null);
public sealed class FlagDraftService(PulseDbContext db, TimeProvider clock, FlagMutationService mutations)
{
    public async Task<FlagDraftOutcome> CloneAsync(Guid projectId, string sourceKey, string key, string name, AuditActor actor, CancellationToken ct)
    {
        var source = await db.FeatureFlags.AsNoTracking().SingleOrDefaultAsync(f => f.ProjectId == projectId && f.Key == sourceKey, ct);
        if (source is null) return new(404);
        if (!FlagConfigurationValidator.IsValidStored(source)) return new(409);
        if (await db.FeatureFlags.AnyAsync(f => f.ProjectId == projectId && f.Key == key, ct)) return new(409);
        var clone = new FeatureFlag { ProjectId = projectId, Key = key, Name = name, Active = false, Type = source.Type,
            RolloutPercentage = source.RolloutPercentage, FiltersJson = source.FiltersJson, VariantsJson = source.VariantsJson,
            CreatedAt = clock.GetUtcNow() };
        var created = await mutations.CreateAsync(clone, actor, ct);
        return new(created.Status, created.Flag);
    }

    public static bool IsProjectKeyConflict(DbUpdateException error) => error.InnerException is SqliteException sqlite &&
        sqlite.SqliteExtendedErrorCode == 2067 && sqlite.Message.Contains("FeatureFlags.ProjectId, FeatureFlags.Key", StringComparison.Ordinal);
}
