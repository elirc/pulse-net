using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionLeaseTests
{
    [Fact]
    public async Task PausedOldOwnerCannotMutateAfterReassignment_AndNewOwnerProcessesOnce()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Admission(seed).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], true, default);
        await using var old = fixture.Open(); var leases = new IngestionLeaseService(old, fixture.Clock);
        var a = (await leases.ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default))!;
        var row = await old.QueuedEvents.AsNoTracking().SingleAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(30);
        await using var replacement = fixture.Open();
        var b = (await new IngestionLeaseService(replacement, fixture.Clock).ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default))!;
        Assert.Equal(a.Generation + 1, b.Generation);
        await Assert.ThrowsAsync<LostIngestionLeaseException>(() => fixture.Processor(old).ProcessLeasedRowAsync(row, fixture.Project, a, default));
        Assert.Equal(0, await old.Events.CountAsync()); Assert.Equal(0, await old.Persons.CountAsync()); Assert.Equal(1, await old.QueuedEvents.CountAsync());
        Assert.Equal("processed", await fixture.Processor(replacement).ProcessLeasedRowAsync(row, fixture.Project, b, default));
        Assert.Equal(1, await replacement.Events.CountAsync()); Assert.Equal(0, await replacement.QueuedEvents.CountAsync());
        Assert.False(await leases.RenewAsync(a, default));
    }

    [Fact]
    public async Task ConcurrentClaimants_OnlyOneOwnsAnUnexpiredLease_AndDifferentProjectsCanHaveDifferentOwners()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IngestionLeaseToken?> Claim()
        {
            await using var db = fixture.Open(); await start.Task;
            return await new IngestionLeaseService(db, fixture.Clock).ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default);
        }
        var a = Task.Run(Claim); var b = Task.Run(Claim); start.SetResult();
        var claimed = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(90));
        var owner = Assert.Single(claimed, c => c is not null)!;
        await using var other = fixture.Open(); var service = new IngestionLeaseService(other, fixture.Clock);
        var different = (await service.ClaimAsync(Guid.NewGuid(), Guid.NewGuid(), default))!;
        Assert.NotEqual(owner.Owner, different.Owner);
        Assert.True(await service.RenewAsync(owner, default));
        await service.ReleaseAsync(owner, default);
        var reclaimed = (await service.ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default))!; Assert.Equal(2, reclaimed.Generation);
    }

    [Fact]
    public async Task StaleOwnerCannotDeadLetterMalformedWork_AndPausedProjectRemainsUntouched()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], false, default);
        await db.QueuedEvents.ExecuteUpdateAsync(s => s.SetProperty(q => q.PayloadJson, "malformed"));
        var row = await db.QueuedEvents.AsNoTracking().SingleAsync(); var leases = new IngestionLeaseService(db, fixture.Clock);
        var first = (await leases.ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default))!;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(31);
        var next = (await leases.ClaimAsync(fixture.Project.Id, Guid.NewGuid(), default))!;
        await Assert.ThrowsAsync<LostIngestionLeaseException>(() => fixture.Processor(db).ProcessLeasedRowAsync(row, fixture.Project, first, default));
        Assert.Equal(0, await db.DeadLetterEvents.CountAsync());
        await db.ProjectIngestionStates.ExecuteUpdateAsync(s => s.SetProperty(p => p.Paused, true));
        await Assert.ThrowsAsync<IngestionPausedException>(() => fixture.Processor(db).ProcessLeasedRowAsync(row, fixture.Project, next, default));
        Assert.Equal(1, await db.QueuedEvents.CountAsync()); Assert.Equal(0, await db.DeadLetterEvents.CountAsync());
    }
}
