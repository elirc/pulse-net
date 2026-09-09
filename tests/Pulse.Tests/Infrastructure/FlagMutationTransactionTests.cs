using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class FlagMutationTransactionTests
{
    private sealed class UpdateSql : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingUpdatesOrDelete_OnlyOneCommitsExpectedRevision(bool deleteSecond)
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var sql = new UpdateSql();
        async Task<int> Change(bool second)
        {
            await using var db = fixture.Open(sql);
            var candidate = await fixture.FlagAsync(db);
            candidate.Name = second ? "second" : "first";
            if (Interlocked.Increment(ref arrived) == 2) reads.TrySetResult();
            await reads.Task.WaitAsync(TimeSpan.FromSeconds(60));
            return second && deleteSecond
                ? await fixture.Mutations(db).DeleteAsync(candidate, 1, fixture.Actor, default)
                : (await fixture.Mutations(db).UpdateAsync(candidate, 1, fixture.Actor, default)).Status;
        }
        var results = await Task.WhenAll(Task.Run(() => Change(false)), Task.Run(() => Change(true))).WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Single(results, status => status is 200 or 204);
        Assert.Single(results, status => status is 404 or 412);
        await using var verify = fixture.Open();
        Assert.Equal(2, await verify.AuditEntries.CountAsync(e => e.ResourceId == fixture.FlagId));
        var remaining = await verify.FeatureFlags.SingleOrDefaultAsync(f => f.Id == fixture.FlagId);
        Assert.Equal(remaining is null ? 0 : 2, await verify.FlagVersions.CountAsync(v => v.FlagId == fixture.FlagId));
        if (remaining is not null) Assert.Equal(2, remaining.Revision);
        var mutation = sql.Commands.First(c => c.StartsWith("UPDATE \"FeatureFlags\"", StringComparison.Ordinal));
        var predicate = mutation[mutation.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains("\"ProjectId\"", predicate);
        Assert.Contains("\"Id\"", predicate);
        Assert.Contains("\"Revision\"", predicate);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("restore")]
    public async Task AuditFailure_RollsBackImmediateSqlAndHistory(string operation)
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        await using (var db = fixture.Open(new FailAuditSave()))
        {
            var flag = await fixture.FlagAsync(db);
            flag.Name = "must roll back";
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                if (operation == "delete") await fixture.Mutations(db).DeleteAsync(flag, 1, fixture.Actor, default);
                else if (operation == "restore") await fixture.Mutations(db).RestoreAsync(flag, 1, 1, fixture.Actor, default);
                else await fixture.Mutations(db).UpdateAsync(flag, 1, fixture.Actor, default);
            });
        }
        await using var verify = fixture.Open();
        var preserved = await fixture.FlagAsync(verify);
        Assert.Equal("initial", preserved.Name);
        Assert.Equal(1, preserved.Revision);
        Assert.Equal(1, await verify.FlagVersions.CountAsync());
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task MembershipAuditFailure_RollsBackRemovalAndRoleChange()
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        var memberId = Guid.NewGuid();
        await using (var seed = fixture.Open())
        {
            seed.ProjectMemberships.Add(new ProjectMembership { ProjectId = fixture.ProjectId, UserId = memberId, Role = ProjectRole.Editor });
            await seed.SaveChangesAsync();
        }
        foreach (var role in new ProjectRole?[] { ProjectRole.Viewer, null })
        {
            await using var db = fixture.Open(new FailAuditSave());
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectMembershipService(db, fixture.Clock)
                .ChangeAsync(fixture.ProjectId, fixture.Actor.UserId, memberId, role, default));
        }
        await using var verify = fixture.Open();
        Assert.Equal(ProjectRole.Editor, (await verify.ProjectMemberships.SingleAsync(m => m.UserId == memberId)).Role);
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }
}
