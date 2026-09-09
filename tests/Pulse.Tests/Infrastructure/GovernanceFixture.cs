using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

internal sealed class GovernanceClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-03-01T10:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now = Now.Add(duration);
}

internal sealed class GovernanceFixture : IAsyncDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"pulse-governance-{Guid.NewGuid():N}.db");
    public Guid ProjectId { get; } = Guid.NewGuid();
    public AuditActor Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid());
    public GovernanceClock Clock { get; } = new();
    public Guid FlagId { get; private set; }
    public PulseDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PulseDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString()).AddInterceptors(interceptors).Options);
    public FlagMutationService Mutations(PulseDbContext db) => new(db, Clock);
    public FlagScheduleService Schedules(PulseDbContext db) => new(db, Mutations(db), new FlagScheduleSignal(), Clock);

    public async Task InitializeAsync()
    {
        await using var db = Open();
        await db.Database.MigrateAsync();
        db.Projects.Add(new Project { Id = ProjectId, Name = "Governance", ApiKey = Guid.NewGuid().ToString(), ReadKey = Guid.NewGuid().ToString() });
        db.ProjectMemberships.Add(new ProjectMembership { ProjectId = ProjectId, UserId = Actor.UserId, Role = ProjectRole.Admin });
        await db.SaveChangesAsync();
        var flag = new FeatureFlag { ProjectId = ProjectId, Key = "flag", Name = "initial", RolloutPercentage = 10 };
        Assert.Equal(201, (await Mutations(db).CreateAsync(flag, Actor, default)).Status);
        FlagId = flag.Id;
    }
    public Task<FeatureFlag> FlagAsync(PulseDbContext db) => db.FeatureFlags.AsNoTracking().SingleAsync(f => f.Id == FlagId);
    public ValueTask DisposeAsync()
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) if (File.Exists(file)) File.Delete(file);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailAuditSave : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<AuditEntry>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("injected audit failure");
        return ValueTask.FromResult(result);
    }
}
