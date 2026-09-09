using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class CaptureIdempotencyTests
{
    [Fact]
    public async Task LostResponseRestartAndExactExpiry_KeepWindowFixedAndProcessingIdentityDistinct()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        var item = new IncomingEvent("event", "p", null, "{\"a\":1,\"b\":2}", Guid.NewGuid());
        var accepted = fixture.Clock.Now;
        Guid original;
        await using (var first = fixture.Open())
        {
            Assert.Equal(1, (await fixture.Admission(first).AdmitAsync(fixture.Project.Id, [item], false, default)).Queued);
            original = (await first.CaptureAdmissionKeys.SingleAsync()).AdmissionId;
        }
        fixture.Clock.Now = accepted.AddDays(6);
        await using (var restarted = fixture.Open())
        {
            var retry = await fixture.Admission(restarted).AdmitAsync(fixture.Project.Id, [item with { PropertiesJson = "{\"b\":2,\"a\":1}" }], true, default);
            Assert.Equal(0, retry.Queued); Assert.Equal(1, retry.Deduplicated);
            Assert.Equal(accepted.AddDays(7), (await restarted.CaptureAdmissionKeys.SingleAsync()).ExpiresAt);
            Assert.Equal(original, (await fixture.Receipts(restarted).ReadAsync(fixture.Project.Id, retry.ReceiptId!.Value, default))!.Items[0].AdmissionId);
            Assert.Equal((1, 0), await fixture.Processor(restarted).ProcessPendingAsync());
        }
        fixture.Clock.Now = accepted.AddDays(7);
        await using var expired = fixture.Open();
        var next = await fixture.Admission(expired).AdmitAsync(fixture.Project.Id, [item], false, default);
        Assert.Equal(1, next.Queued); Assert.NotEqual(original, (await expired.CaptureAdmissionKeys.SingleAsync()).AdmissionId);
        Assert.Equal((1, 0), await fixture.Processor(expired).ProcessPendingAsync());
        Assert.Equal(2, await expired.Events.CountAsync());
    }

    [Fact]
    public async Task ConflictingKeys_RejectWholeBatch_AndEquivalentPositionsCollapse()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var a = new IncomingEvent("a", "p", null, "{}", Guid.NewGuid());
        var b = new IncomingEvent("b", "p", null, "{}", Guid.NewGuid());
        var accepted = await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [a, a, a], true, default);
        Assert.Equal(1, accepted.Queued); Assert.Equal(2, accepted.Deduplicated);
        var receipt = (await fixture.Receipts(db).ReadAsync(fixture.Project.Id, accepted.ReceiptId!.Value, default))!;
        Assert.Equal(3, receipt.Queued); Assert.Single(receipt.Items.Select(i => i.AdmissionId).Distinct());
        var conflict = await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [b, a with { Name = "changed" }], true, default);
        Assert.Equal(409, conflict.Status); Assert.Equal("event_id_payload_conflict", conflict.Code);
        Assert.Equal(1, await db.QueuedEvents.CountAsync()); Assert.Equal(1, await db.CaptureReceipts.CountAsync()); Assert.Equal(1, await db.CaptureAdmissionKeys.CountAsync());
        var inside = await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [b, b with { Name = "other" }], true, default);
        Assert.Equal(409, inside.Status); Assert.Equal(1, await db.QueuedEvents.CountAsync());
        Assert.Equal(2, (await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [a with { ClientEventId = null }, a with { ClientEventId = null }], false, default)).Queued);
        Assert.Equal(1, (await fixture.Admission(db).AdmitAsync(Guid.NewGuid(), [a], false, default)).Queued);
    }

    [Fact]
    public async Task ConcurrentSubmissions_WithOneClientId_QueueExactlyOnce()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        var item = new IncomingEvent("a", "p", null, "{}", Guid.NewGuid());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CaptureAdmissionOutcome> Submit()
        {
            await using var db = fixture.Open(); await start.Task;
            return await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [item], true, default);
        }
        var first = Task.Run(Submit); var second = Task.Run(Submit); start.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(90));
        Assert.All(results, r => Assert.Equal(202, r.Status)); Assert.Equal(1, results.Sum(r => r.Queued)); Assert.Equal(1, results.Sum(r => r.Deduplicated));
        await using var verify = fixture.Open(); Assert.Equal(1, await verify.QueuedEvents.CountAsync()); Assert.Equal(2, await verify.CaptureReceipts.CountAsync());
        Assert.Equal(1, await verify.CaptureProcessingItems.CountAsync());
    }

    [Fact]
    public async Task LegacyKeyWithoutMetadata_RejectsReceiptBatchBeforeNewAdmission()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync(); await using var db = fixture.Open();
        var item = new IncomingEvent("a", "p", null, "{}", Guid.NewGuid());
        Assert.True(CaptureFingerprint.TryCreate(item.Name, item.DistinctId, item.Timestamp, item.PropertiesJson, out var hash));
        db.CaptureAdmissionKeys.Add(new CaptureAdmissionKey { ProjectId = fixture.Project.Id, ClientEventId = item.ClientEventId!.Value,
            AdmissionId = Guid.NewGuid(), PayloadHash = hash, AcceptedAt = fixture.Clock.Now, ExpiresAt = fixture.Clock.Now.AddDays(7) });
        await db.SaveChangesAsync();
        var result = await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [item with { ClientEventId = Guid.NewGuid() }, item], true, default);
        Assert.Equal(409, result.Status); Assert.Equal("legacy_processing_metadata_unavailable", result.Code);
        Assert.Equal(0, await db.QueuedEvents.CountAsync()); Assert.Equal(0, await db.CaptureReceipts.CountAsync());
        Assert.Equal(1, (await fixture.Admission(db).AdmitAsync(fixture.Project.Id, [item], false, default)).Deduplicated);
    }
}
