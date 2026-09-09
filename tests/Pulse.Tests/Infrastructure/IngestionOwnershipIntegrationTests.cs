using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionOwnershipIntegrationTests
{
    [Fact]
    public async Task TwoActualProcessors_PreserveIdentityMergeAndAcknowledgeEachEventOnce()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        await using (var setup = f.Open())
            await f.Admission(setup).AdmitAsync(f.Project.Id, [new("view", "anon", null, "{}"), new("login", "known", null, "{}"),
                new("$identify", "known", null, "{\"$anon_distinct_id\":\"anon\"}"), new("purchase", "anon", null, "{}")], true, default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(int Processed, int DeadLettered)> Run() { await start.Task; await using var db = f.Open(); return await f.Processor(db).ProcessPendingAsync(); }
        var a = Task.Run(Run); var b = Task.Run(Run); start.SetResult();
        var results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(4, results.Sum(r => r.Processed)); Assert.Equal(0, results.Sum(r => r.DeadLettered));
        await using var verify = f.Open(); var person = Assert.Single(await verify.Persons.ToListAsync());
        Assert.Equal(4, await verify.Events.CountAsync()); Assert.All(await verify.Events.ToListAsync(), row => Assert.Equal(person.Id, row.PersonId));
        Assert.Equal(2, await verify.PersonDistinctIds.CountAsync()); Assert.Empty(await verify.QueuedEvents.ToListAsync());
        var receipt = await verify.CaptureReceipts.SingleAsync(); Assert.Equal(4, (await f.Receipts(verify).ReadAsync(f.Project.Id, receipt.Id, default))!.Processed);
    }

    [Fact]
    public async Task AHotProjectDoesNotConsumeTheWholeCycleBeforeASmallProjectGetsWork()
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        var small = new Project { Name = "Small", ApiKey = Guid.NewGuid().ToString(), ReadKey = Guid.NewGuid().ToString() }; db.Projects.Add(small); await db.SaveChangesAsync();
        await f.Admission(db).AdmitAsync(f.Project.Id, Enumerable.Range(0, 201).Select(_ => new IncomingEvent("hot", "p", null, "{}")).ToArray(), false, default);
        await f.Admission(db).AdmitAsync(small.Id, [new("small", "p", null, "{}")], false, default);
        var result = await f.Processor(db).ProcessPendingAsync(); Assert.Equal(51, result.Processed);
        Assert.Equal(1, await db.Events.CountAsync(e => e.ProjectId == small.Id)); Assert.Equal(151, await db.QueuedEvents.CountAsync(q => q.ProjectId == f.Project.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionOrDeadLetterSaveFailure_RollsBackEveryLinkedWrite(bool failDeadLetter)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); var input = new IncomingEvent("view", "p", null, "{}", Guid.NewGuid());
        if (!failDeadLetter)
        {
            await using (var failing = f.Open(new FailReliabilitySave(false)))
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Admission(failing).AdmitAsync(f.Project.Id, [input], true, default));
            await using var verify = f.Open();
            Assert.Empty(await verify.QueuedEvents.ToListAsync()); Assert.Empty(await verify.CaptureAdmissionKeys.ToListAsync()); Assert.Empty(await verify.CaptureProcessingItems.ToListAsync());
            Assert.Empty(await verify.CaptureReceipts.ToListAsync()); Assert.Empty(await verify.CaptureReceiptItems.ToListAsync());
            Assert.Equal(202, (await f.Admission(verify).AdmitAsync(f.Project.Id, [input], true, default)).Status);
        }
        else
        {
            await using (var setup = f.Open()) { await f.Admission(setup).AdmitAsync(f.Project.Id, [input], true, default); await setup.QueuedEvents.ExecuteUpdateAsync(s => s.SetProperty(q => q.PayloadJson, "invalid")); }
            await using (var failing = f.Open(new FailReliabilitySave(true)))
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Processor(failing).ProcessPendingAsync());
            await using var verify = f.Open();
            Assert.Single(await verify.QueuedEvents.ToListAsync()); Assert.Empty(await verify.DeadLetterEvents.ToListAsync()); Assert.Empty(await verify.CaptureItemTransitions.ToListAsync());
            var item = await verify.CaptureProcessingItems.SingleAsync(); Assert.Equal(ProcessingItemState.Queued, item.State); Assert.Null(item.DeadLetterId);
            Assert.Equal(1, (await f.Processor(verify).ProcessPendingAsync()).DeadLettered);
        }
    }

    private sealed class FailReliabilitySave(bool deadLetter) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().Where(e => e.State == EntityState.Added);
            if (entries.Any(e => deadLetter ? e.Entity is DeadLetterEvent : e.Entity is CaptureProcessingItem)) throw new InvalidOperationException("injected reliability failure");
            return ValueTask.FromResult(result);
        }
    }
}
