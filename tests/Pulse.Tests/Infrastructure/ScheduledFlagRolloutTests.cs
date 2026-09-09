using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;

namespace Pulse.Tests.Infrastructure;

public class ScheduledFlagRolloutTests
{
    [Fact]
    public async Task DueEquality_RestartAndRepeatedSweep_ApplyExactlyOnceWithHistoryAndAudit()
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        var due = fixture.Clock.Now.AddMinutes(1);
        Guid id;
        await using (var db = fixture.Open())
        {
            var created = await fixture.Schedules(db).CreateAsync(fixture.ProjectId, "flag", due, 50, 1, fixture.Actor, default);
            Assert.Equal(202, created.Status); id = created.Schedule!.Id;
        }
        fixture.Clock.Now = due.AddTicks(-1);
        await using (var early = fixture.Open()) Assert.Equal(0, await fixture.Schedules(early).ProcessDueAsync(default));
        fixture.Clock.Now = due;
        await using (var restarted = fixture.Open()) Assert.Equal(1, await fixture.Schedules(restarted).ProcessDueAsync(default));
        fixture.Clock.Now = due.AddHours(2);
        await using var verify = fixture.Open();
        Assert.Equal(0, await fixture.Schedules(verify).ProcessDueAsync(default));
        var flag = await fixture.FlagAsync(verify);
        Assert.Equal(2, flag.Revision); Assert.Equal(50, flag.RolloutPercentage); Assert.Equal("initial", flag.Name); Assert.True(flag.Active);
        var schedule = await verify.FlagRolloutSchedules.SingleAsync(s => s.Id == id);
        Assert.Equal(FlagScheduleStatus.Applied, schedule.Status); Assert.Equal(2, schedule.AppliedRevision);
        Assert.Equal(2, await verify.FlagVersions.CountAsync()); Assert.Equal(2, await verify.AuditEntries.CountAsync());
        Assert.Equal(409, await fixture.Schedules(verify).CancelAsync(fixture.ProjectId, fixture.FlagId, fixture.Actor, default));
        var replacement = await fixture.Schedules(verify).CreateAsync(fixture.ProjectId, "flag", fixture.Clock.Now.AddDays(1), 80, 2, fixture.Actor, default);
        Assert.Equal(202, replacement.Status); Assert.NotEqual(id, replacement.Schedule!.Id); Assert.Equal(1, await verify.FlagRolloutSchedules.CountAsync());
    }

    [Theory]
    [InlineData("edit", FlagScheduleStatus.Conflict, "revision_changed")]
    [InlineData("delete", FlagScheduleStatus.Conflict, "flag_deleted")]
    [InlineData("demote", FlagScheduleStatus.Blocked, "creator_no_longer_editor")]
    public async Task ChangedPrerequisites_OnlyChangeScheduleOutcome(string change, FlagScheduleStatus status, string reason)
    {
        await using var fixture = new GovernanceFixture(); await fixture.InitializeAsync();
        await using (var db = fixture.Open())
        {
            await fixture.Schedules(db).CreateAsync(fixture.ProjectId, "flag", fixture.Clock.Now.AddMinutes(1), 50, 1, fixture.Actor, default);
            if (change == "edit") await fixture.Mutations(db).UpdateAsync(await fixture.FlagAsync(db), 1, fixture.Actor, default);
            if (change == "delete") await fixture.Mutations(db).DeleteAsync(await fixture.FlagAsync(db), 1, fixture.Actor, default);
            if (change == "demote") await db.ProjectMemberships.ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ProjectRole.Viewer));
        }
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        await using var verify = fixture.Open();
        var audits = await verify.AuditEntries.CountAsync();
        Assert.Equal(1, await fixture.Schedules(verify).ProcessDueAsync(default));
        var schedule = await verify.FlagRolloutSchedules.SingleAsync();
        Assert.Equal(status, schedule.Status); Assert.Equal(reason, schedule.Reason); Assert.Null(schedule.AppliedRevision);
        Assert.Equal(audits, await verify.AuditEntries.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingProcessorOrCancellation_LeavesOneTerminalOutcome(bool cancellation)
    {
        await using var fixture = new GovernanceFixture(); await fixture.InitializeAsync();
        Guid id;
        await using (var seed = fixture.Open()) id = (await fixture.Schedules(seed).CreateAsync(fixture.ProjectId, "flag", fixture.Clock.Now.AddMinutes(1), 50, 1, fixture.Actor, default)).Schedule!.Id;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Execute(bool second)
        {
            await using var db = fixture.Open();
            await start.Task;
            if (second && cancellation) Assert.Contains(await fixture.Schedules(db).CancelAsync(fixture.ProjectId, fixture.FlagId, fixture.Actor, default), new[] { 204, 409 });
            else await fixture.Schedules(db).ProcessOneAsync(id, default);
        }
        var first = Task.Run(() => Execute(false)); var other = Task.Run(() => Execute(true)); start.SetResult();
        await Task.WhenAll(first, other).WaitAsync(TimeSpan.FromSeconds(90));
        await using var verify = fixture.Open();
        var state = (await verify.FlagRolloutSchedules.SingleAsync()).Status;
        Assert.Contains(state, cancellation ? new[] { FlagScheduleStatus.Applied, FlagScheduleStatus.Cancelled } : new[] { FlagScheduleStatus.Applied });
        var expected = state == FlagScheduleStatus.Applied ? 2 : 1;
        Assert.Equal(expected, (await fixture.FlagAsync(verify)).Revision);
        Assert.Equal(expected, await verify.FlagVersions.CountAsync()); Assert.Equal(expected, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task FailedAuditSave_KeepsSchedulePendingAndFlagUnchanged_ThenRetrySucceeds()
    {
        await using var fixture = new GovernanceFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Schedules(seed).CreateAsync(fixture.ProjectId, "flag", fixture.Clock.Now.AddMinutes(1), 50, 1, fixture.Actor, default);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        await using (var failing = fixture.Open(new FailAuditSave())) await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Schedules(failing).ProcessDueAsync(default));
        await using var verify = fixture.Open();
        Assert.Equal(FlagScheduleStatus.Pending, (await verify.FlagRolloutSchedules.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, (await fixture.FlagAsync(verify)).Revision);
        Assert.Equal(1, await verify.FlagVersions.CountAsync());
        Assert.Equal(1, await fixture.Schedules(verify).ProcessDueAsync(default));
        Assert.Equal(2, (await fixture.FlagAsync(verify)).Revision);
    }

    [Fact]
    public async Task Admission_ValidatesTimePercentageRevisionAndOnePendingSchedule()
    {
        await using var fixture = new GovernanceFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var service = fixture.Schedules(db); var now = fixture.Clock.Now;
        Assert.Equal(400, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddSeconds(59), 10, 1, fixture.Actor, default)).Status);
        Assert.Equal(400, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddDays(30).AddTicks(1), 10, 1, fixture.Actor, default)).Status);
        Assert.Equal(400, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddDays(1), 101, 1, fixture.Actor, default)).Status);
        Assert.Equal(412, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddDays(1), 10, 2, fixture.Actor, default)).Status);
        Assert.Equal(202, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddDays(30), 0, 1, fixture.Actor, default)).Status);
        Assert.Equal(409, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddDays(1), 100, 1, fixture.Actor, default)).Status);
        Assert.Equal(204, await service.CancelAsync(fixture.ProjectId, fixture.FlagId, fixture.Actor, default));
        Assert.Equal(202, (await service.CreateAsync(fixture.ProjectId, "flag", now.AddMinutes(1), 100, 1, fixture.Actor, default)).Status);
    }
}
