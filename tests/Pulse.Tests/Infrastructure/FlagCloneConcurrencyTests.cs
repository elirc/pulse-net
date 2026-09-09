using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class FlagCloneConcurrencyTests
{
    private sealed class SaveTogether : SaveChangesInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task TwoClonesPassPrecheck_ButUniqueIndexPermitsOnlyOneTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulse-clone-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString();
        try
        {
            var options = new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options;
            var project = Guid.NewGuid();
            var actor = new AuditActor(Guid.NewGuid());
            var source = new FeatureFlag { ProjectId = project, Key = "source", Name = "Unchanged" };
            await using (var seed = new PulseDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.FeatureFlags.Add(source);
                seed.ProjectMemberships.Add(new ProjectMembership { ProjectId = project, UserId = actor.UserId, Role = ProjectRole.Admin });
                await seed.SaveChangesAsync();
            }
            var barrier = new SaveTogether();
            var competing = new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).AddInterceptors(barrier).Options;
            async Task<int> Clone()
            {
                await using var db = new PulseDbContext(competing);
                return (await new FlagDraftService(db, TimeProvider.System, new FlagMutationService(db, TimeProvider.System)).CloneAsync(project, "source", "target", "Draft", actor, default)).Status;
            }
            var outcomes = await Task.WhenAll(Task.Run(Clone), Task.Run(Clone)).WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(new[] { 201, 409 }, outcomes.Order());
            await using var verify = new PulseDbContext(options);
            Assert.Equal(1, await verify.FeatureFlags.CountAsync(f => f.ProjectId == project && f.Key == "target"));
            var unchanged = await verify.FeatureFlags.SingleAsync(f => f.Id == source.Id);
            Assert.True(unchanged.Active);
            Assert.Equal(source.Name, unchanged.Name);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
