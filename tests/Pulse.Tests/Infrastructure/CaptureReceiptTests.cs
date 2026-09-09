using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class CaptureReceiptTests
{
    [Fact]
    public async Task MixedOutcomesAndReplay_PreserveLogicalItemAndExposeActualPointers()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var accepted = await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [new("good", "p", null, "{}"), new("bad", "p", null, "{}")], true, default);
        var initial = (await fixture.Receipts(db).ReadAsync(fixture.Project.Id, accepted.ReceiptId!.Value, default))!;
        Assert.Equal(2, initial.Queued); Assert.Null(initial.CompletedAt); Assert.Null(initial.ExpiresAt);
        var failedItem = initial.Items[1].AdmissionId;
        await db.QueuedEvents.Where(q => q.AdmissionId == failedItem).ExecuteUpdateAsync(s => s.SetProperty(q => q.PayloadJson, "malformed"));
        Assert.Equal((1, 1), await fixture.Processor(db).ProcessPendingAsync());
        var terminal = (await fixture.Receipts(db).ReadAsync(fixture.Project.Id, accepted.ReceiptId.Value, default))!;
        Assert.Equal((0, 1, 1), (terminal.Queued, terminal.Processed, terminal.DeadLettered));
        Assert.Equal(fixture.Clock.Now.AddDays(7), terminal.ExpiresAt);
        Assert.Equal((await db.Events.SingleAsync()).Id, terminal.Items[0].EventId);
        var letter = await db.DeadLetterEvents.SingleAsync(); Assert.Equal(letter.Id, terminal.Items[1].DeadLetterId); Assert.Equal(1, letter.Attempts);
        // Repair fixture corruption to model a now-replayable stored failure.
        var repaired = System.Text.Json.JsonSerializer.Serialize(new IncomingEvent("repaired", "p", null, "{}"));
        await db.DeadLetterEvents.Where(d => d.Id == letter.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.PayloadJson, repaired));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        Assert.Equal(DeadLetterReplayOutcome.Queued, await fixture.Operations(db).ReplayAsync(fixture.Project.Id, letter.Id, default));
        var replaying = (await fixture.Receipts(db).ReadAsync(fixture.Project.Id, accepted.ReceiptId.Value, default))!;
        Assert.Null(replaying.CompletedAt); Assert.Null(replaying.ExpiresAt); Assert.Equal(1, replaying.Queued);
        Assert.Equal(failedItem, replaying.Items[1].AdmissionId); Assert.Equal(1, replaying.Items[1].ReplayGeneration); Assert.Null(replaying.Items[1].DeadLetterId);
        Assert.Equal((1, 0), await fixture.Processor(db).ProcessPendingAsync());
        var done = (await fixture.Receipts(db).ReadAsync(fixture.Project.Id, accepted.ReceiptId.Value, default))!;
        Assert.Equal(2, done.Processed); Assert.Equal(0, done.DeadLettered); Assert.Equal(fixture.Clock.Now.AddDays(7), done.ExpiresAt);
        Assert.Equal(3, await db.CaptureItemTransitions.CountAsync(t => t.AdmissionId == failedItem));
        Assert.Null(await fixture.Receipts(db).ReadAsync(Guid.NewGuid(), accepted.ReceiptId.Value, default));
    }

    [Fact]
    public async Task CrashBeforeAcknowledgement_KeepsReceiptQueuedAndNoEvent_ThenRetrySucceeds()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        Guid receiptId;
        await using (var db = fixture.Open())
        {
            receiptId = (await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [new("a", "p", null, "{}")], true, default)).ReceiptId!.Value;
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_ack BEFORE DELETE ON QueuedEvents BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Processor(db).ProcessPendingAsync());
        }
        await using var restarted = fixture.Open();
        var queued = (await fixture.Receipts(restarted).ReadAsync(fixture.Project.Id, receiptId, default))!;
        Assert.Equal(1, queued.Queued); Assert.Null(queued.Items[0].EventId);
        Assert.Equal(0, await restarted.Events.CountAsync()); Assert.Equal(0, await restarted.Persons.CountAsync()); Assert.Equal(0, await restarted.CaptureItemTransitions.CountAsync());
        Assert.Equal(0, (await restarted.QueuedEvents.SingleAsync()).Attempts);
        await restarted.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_ack");
        Assert.Equal((1, 0), await fixture.Processor(restarted).ProcessPendingAsync());
        Assert.Equal(1, (await fixture.Receipts(restarted).ReadAsync(fixture.Project.Id, receiptId, default))!.Processed);
    }

    [Fact]
    public async Task Cleanup_RetainsUnfinishedWorkAndLiveSharedReferences_ThenRemovesOnlyExpiredMetadata()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        Guid receiptId;
        await using (var db = fixture.Open()) receiptId = (await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [new("a", "p", null, "{}", Guid.NewGuid())], true, default)).ReceiptId!.Value;
        fixture.Clock.Now = fixture.Clock.Now.AddDays(8);
        await using (var cleanup = fixture.Open())
        {
            await fixture.Receipts(cleanup).CleanupAsync(default);
            Assert.Equal(1, await cleanup.CaptureReceipts.CountAsync()); Assert.Equal(1, await cleanup.CaptureProcessingItems.CountAsync()); Assert.Equal(0, await cleanup.CaptureAdmissionKeys.CountAsync());
            Assert.Equal(1, (await fixture.Receipts(cleanup).ReadAsync(fixture.Project.Id, receiptId, default))!.Queued);
            Assert.Equal((1, 0), await fixture.Processor(cleanup).ProcessPendingAsync());
        }
        fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        await using var terminal = fixture.Open();
        Assert.Null(await fixture.Receipts(terminal).ReadAsync(fixture.Project.Id, receiptId, default));
        await fixture.Receipts(terminal).CleanupAsync(default);
        Assert.Equal(0, await terminal.CaptureReceipts.CountAsync()); Assert.Equal(0, await terminal.CaptureReceiptItems.CountAsync());
        Assert.Equal(0, await terminal.CaptureProcessingItems.CountAsync()); Assert.Equal(0, await terminal.CaptureItemTransitions.CountAsync());
        Assert.Equal(1, await terminal.Events.CountAsync());
    }
}
