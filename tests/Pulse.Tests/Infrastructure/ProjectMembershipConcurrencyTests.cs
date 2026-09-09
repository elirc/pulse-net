using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ProjectMembershipConcurrencyTests
{
    private sealed class StartTogether : DbTransactionInterceptor
    {
        private int arrived;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrived) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return result;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TwoAdminsCannotBothRemoveTheirOwnAdminRole(bool remove)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulse-members-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 2 }.ToString();
        try
        {
            var options = new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options;
            var project = new Project { Name = "Last administrator", ApiKey = "pk_fixture", ReadKey = "rk_fixture" };
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            await using (var seed = new PulseDbContext(options))
            {
                await seed.Database.MigrateAsync();
                seed.Projects.Add(project);
                seed.ProjectMemberships.AddRange(new ProjectMembership { ProjectId = project.Id, UserId = first, Role = ProjectRole.Admin },
                    new ProjectMembership { ProjectId = project.Id, UserId = second, Role = ProjectRole.Admin });
                await seed.SaveChangesAsync();
            }
            var barrier = new StartTogether();
            var competing = new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).AddInterceptors(barrier).Options;
            async Task<int> Change(Guid actor)
            {
                await using var db = new PulseDbContext(competing);
                try { return (await new ProjectMembershipService(db, TimeProvider.System)
                    .ChangeAsync(project.Id, actor, actor, remove ? null : ProjectRole.Viewer, default)).Status; }
                catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { return 503; }
            }
            var statuses = await Task.WhenAll(Task.Run(() => Change(first)), Task.Run(() => Change(second))).WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Single(statuses, status => status == (remove ? 204 : 200));
            Assert.Single(statuses, status => status is 409 or 503);
            await using var verify = new PulseDbContext(options);
            Assert.Equal(1, await verify.ProjectMemberships.CountAsync(m => m.ProjectId == project.Id && m.Role == ProjectRole.Admin));
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
