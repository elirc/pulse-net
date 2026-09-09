using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ProjectQueueCapacityTests
{
    [Fact]
    public async Task Capacity_CountsOnlyNewWork_AndRejectedBatchDoesNotConsumeKeysOrReceipts()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var service = fixture.Admission(db); await service.SetLimitAsync(fixture.Project.Id, 3, default);
        var item = new IncomingEvent("a", "p", null, "{}", Guid.NewGuid());
        Assert.Equal(2, (await service.AdmitAsync(fixture.Project.Id, [item, item with { ClientEventId = Guid.NewGuid() }], true, default)).Queued);
        var blocked = await service.AdmitAsync(fixture.Project.Id, [item with { ClientEventId = Guid.NewGuid() }, item with { ClientEventId = Guid.NewGuid() }], true, default);
        Assert.Equal(429, blocked.Status); Assert.Equal("queue_capacity_exceeded", blocked.Code);
        Assert.Equal(2, await db.QueuedEvents.CountAsync()); Assert.Equal(2, await db.CaptureAdmissionKeys.CountAsync()); Assert.Equal(1, await db.CaptureReceipts.CountAsync());
        await service.SetLimitAsync(fixture.Project.Id, 1, default);
        Assert.Equal(2, (await service.ReadLimitsAsync(fixture.Project.Id, default)).Pending);
        Assert.Equal(202, (await service.AdmitAsync(fixture.Project.Id, [item, item], true, default)).Status);
        Assert.Equal(429, (await service.AdmitAsync(fixture.Project.Id, [item with { ClientEventId = null }], false, default)).Status);
        await service.SetLimitAsync(fixture.Project.Id, 3, default);
        Assert.Equal(1, (await service.AdmitAsync(fixture.Project.Id, [item, item with { ClientEventId = null }], false, default)).Queued);
        Assert.Equal(3, await db.QueuedEvents.CountAsync());
        Assert.Equal((3, 0), await fixture.Processor(db).ProcessPendingAsync());
        Assert.Equal(2, (await service.AdmitAsync(fixture.Project.Id, [item with { ClientEventId = null }, item with { ClientEventId = null }], false, default)).Queued);
    }

    [Fact]
    public async Task Replay_UsesSameCapacityAndRetainsLetterUntilThereIsRoom()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var item = new IncomingEvent("a", "p", null, "{}"); var admission = fixture.Admission(db);
        await admission.SetLimitAsync(fixture.Project.Id, 1, default);
        await admission.AdmitAsync(fixture.Project.Id, [item], false, default);
        var letter = new DeadLetterEvent { ProjectId = fixture.Project.Id, PayloadJson = System.Text.Json.JsonSerializer.Serialize(item), Error = "retry" };
        db.DeadLetterEvents.Add(letter); await db.SaveChangesAsync();
        Assert.Equal(DeadLetterReplayOutcome.CapacityExceeded, await fixture.Operations(db).ReplayAsync(fixture.Project.Id, letter.Id, default));
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id)); Assert.Equal(1, await db.QueuedEvents.CountAsync());
        await fixture.Processor(db).ProcessPendingAsync();
        Assert.Equal(DeadLetterReplayOutcome.Queued, await fixture.Operations(db).ReplayAsync(fixture.Project.Id, letter.Id, default));
        Assert.False(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id)); Assert.Equal(1, await db.QueuedEvents.CountAsync());
    }

    [Fact]
    public async Task ConcurrentAdmissions_CannotSpendTheSameLastSlot()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Admission(seed).SetLimitAsync(fixture.Project.Id, 1, default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Submit()
        {
            await using var db = fixture.Open(); await start.Task;
            return (await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [new("a", "p", null, "{}")], false, default)).Status;
        }
        var a = Task.Run(Submit); var b = Task.Run(Submit); start.SetResult();
        Assert.Equal(new[] { 202, 429 }, (await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(90))).Order());
        await using var verify = fixture.Open(); Assert.Equal(1, await verify.QueuedEvents.CountAsync());
    }
}
