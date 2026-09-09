using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Infrastructure.Services;

public record IngestionLeaseToken(Guid ProjectId, Guid Owner, long Generation);
public sealed class IngestionWorkerIdentity { public Guid Owner { get; } = Guid.NewGuid(); }
public sealed class LostIngestionLeaseException() : Exception("The project ingestion lease is no longer owned by this processor.");
public sealed class IngestionPausedException() : Exception("Project ingestion is paused.");

public sealed class IngestionLeaseService(PulseDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    public async Task<IngestionLeaseToken?> ClaimAsync(Guid projectId, Guid owner, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProjectIngestionLeases (ProjectId, Owner, Generation, ExpiresAt, LastServedAt) VALUES ({projectId}, NULL, 0, 0, 0) ON CONFLICT(ProjectId) DO NOTHING", ct);
        var now = clock.GetUtcNow(); var expires = now.Add(Duration);
        var claimed = await db.ProjectIngestionLeases.Where(l => l.ProjectId == projectId && l.Generation < long.MaxValue && (l.Owner == null || l.ExpiresAt <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, owner).SetProperty(l => l.Generation, l => l.Generation + 1)
                .SetProperty(l => l.ExpiresAt, expires).SetProperty(l => l.LastServedAt, now), ct);
        if (claimed == 0) return null;
        var generation = await db.ProjectIngestionLeases.Where(l => l.ProjectId == projectId).Select(l => l.Generation).SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return new(projectId, owner, generation);
    }

    public async Task<bool> RenewAsync(IngestionLeaseToken token, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var expires = now.Add(Duration);
        return await db.ProjectIngestionLeases.Where(l => l.ProjectId == token.ProjectId && l.Owner == token.Owner && l.Generation == token.Generation && l.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, expires), ct) == 1;
    }

    public async Task FenceAsync(IngestionLeaseToken token, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Ingestion fencing requires a transaction held through commit.");
        var now = clock.GetUtcNow();
        var owned = await db.ProjectIngestionLeases.Where(l => l.ProjectId == token.ProjectId && l.Owner == token.Owner && l.Generation == token.Generation && l.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Generation, l => l.Generation), ct);
        if (owned != 1) throw new LostIngestionLeaseException();
        if (await db.ProjectIngestionStates.AnyAsync(s => s.ProjectId == token.ProjectId && s.Paused, ct)) throw new IngestionPausedException();
    }

    public Task ReleaseAsync(IngestionLeaseToken token, CancellationToken ct) => db.ProjectIngestionLeases
        .Where(l => l.ProjectId == token.ProjectId && l.Owner == token.Owner && l.Generation == token.Generation)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, (Guid?)null).SetProperty(l => l.ExpiresAt, clock.GetUtcNow()), ct);
}
