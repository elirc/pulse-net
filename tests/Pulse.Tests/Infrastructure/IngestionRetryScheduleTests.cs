using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionRetryScheduleTests
{
    private sealed class FailEventSave(int failures, Func<Exception> error) : SaveChangesInterceptor
    {
        private int remaining = failures;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AnalyticsEvent>().Any(e => e.State == EntityState.Added) && remaining-- > 0) throw error();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task BackoffSurvivesNewProcessor_AndOnlyRunsAtDueEquality()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Admission(seed).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], true, default);
        var accepted = fixture.Clock.Now;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var failing = fixture.Open(new FailEventSave(1, () => new DbUpdateException("injected", new SqliteException("busy", 5))));
            Assert.Equal((0, 0), await fixture.Processor(failing).ProcessPendingAsync());
            var row = await failing.QueuedEvents.AsNoTracking().SingleAsync();
            Assert.Equal(attempt, row.Attempts); Assert.Equal(accepted, row.EnqueuedAt);
            Assert.Equal(fixture.Clock.Now.AddSeconds(attempt == 1 ? 1 : 2), row.NextAttemptAt); Assert.Equal("sqlite_contention", row.LastErrorCode);
            Assert.Equal(0, await failing.Events.CountAsync());
            fixture.Clock.Now = row.NextAttemptAt!.Value.AddTicks(-1);
            await using var early = fixture.Open(); Assert.Equal((0, 0), await fixture.Processor(early).ProcessPendingAsync());
            Assert.Equal(attempt, (await early.QueuedEvents.SingleAsync()).Attempts);
            fixture.Clock.Now = row.NextAttemptAt.Value;
        }
        await using var recovered = fixture.Open(); Assert.Equal((1, 0), await fixture.Processor(recovered).ProcessPendingAsync());
        Assert.Equal(1, await recovered.Events.CountAsync()); Assert.Equal(0, await recovered.QueuedEvents.CountAsync());
        Assert.Equal(ProcessingItemState.Processed, (await recovered.CaptureProcessingItems.SingleAsync()).State);
    }

    [Fact]
    public async Task FifthTransientFailure_RecordsFiveFailuresAndOneDeadLetter()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Admission(seed).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], true, default);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var db = fixture.Open(new FailEventSave(1, () => new SqliteException("locked", 6)));
            Assert.Equal(attempt == 5 ? (0, 1) : (0, 0), await fixture.Processor(db).ProcessPendingAsync());
            if (attempt < 5) fixture.Clock.Now = (await db.QueuedEvents.SingleAsync()).NextAttemptAt!.Value;
        }
        await using var verify = fixture.Open(); var letter = await verify.DeadLetterEvents.SingleAsync();
        Assert.Equal(5, letter.Attempts); Assert.StartsWith("sqlite_contention_exhausted", letter.Error);
        Assert.Equal(0, await verify.Events.CountAsync()); Assert.Equal(0, await verify.QueuedEvents.CountAsync());
        Assert.Equal(ProcessingItemState.DeadLettered, (await verify.CaptureProcessingItems.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownFailureOrCancellation_DoesNotConsumeAttempt(bool cancellation)
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open()) await fixture.Admission(seed).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], false, default);
        await using (var failing = fixture.Open(new FailEventSave(1, () => cancellation ? new OperationCanceledException() : new InvalidOperationException("unknown"))))
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Processor(failing).ProcessPendingAsync());
        await using var verify = fixture.Open(); var row = await verify.QueuedEvents.SingleAsync();
        Assert.Equal(0, row.Attempts); Assert.Null(row.NextAttemptAt); Assert.Equal(0, await verify.DeadLetterEvents.CountAsync()); Assert.Equal(0, await verify.Events.CountAsync());
        Assert.Equal((1, 0), await fixture.Processor(verify).ProcessPendingAsync());
    }

    [Fact]
    public async Task FailedRetryStatePersistence_PreservesOriginalQueueState()
    {
        await using var fixture = new IngestionReliabilityFixture(); await fixture.InitializeAsync();
        await using (var seed = fixture.Open())
        {
            await fixture.Admission(seed).AdmitAsync(fixture.Project.Id, [new("e", "p", null, "{}")], false, default);
            await seed.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_retry BEFORE UPDATE ON QueuedEvents BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        }
        await using (var failing = fixture.Open(new FailEventSave(1, () => new SqliteException("busy", 5))))
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Processor(failing).ProcessPendingAsync());
        await using var verify = fixture.Open(); var row = await verify.QueuedEvents.SingleAsync();
        Assert.Equal(0, row.Attempts); Assert.Null(row.NextAttemptAt); Assert.Null(row.LastErrorCode);
        Assert.Equal(0, await verify.Events.CountAsync()); Assert.Equal(0, await verify.DeadLetterEvents.CountAsync());
    }
}

public class IngestionRetryPolicyTests
{
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 4, false)]
    [InlineData(3, 8, false)]
    [InlineData(4, 0, true)]
    public void FailedAttemptNumberDefinesDelay(int previous, int seconds, bool terminal)
    {
        var result = IngestionRetryPolicy.AfterTransientFailure(previous);
        Assert.Equal(previous + 1, result.FailedAttempts); Assert.Equal(terminal, result.DeadLetter); Assert.Equal(seconds, result.Delay?.TotalSeconds ?? 0);
    }
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(19, false)]
    [InlineData(10, false)]
    public void OnlyRecognizedSqliteContentionIsTransient(int code, bool transient)
    {
        Assert.Equal(transient, IngestionRetryPolicy.IsTransient(new SqliteException("message does not decide category", code)));
        Assert.Equal(transient, IngestionRetryPolicy.IsTransient(new DbUpdateException("outer", new SqliteException("inner", code))));
        Assert.False(IngestionRetryPolicy.IsTransient(new InvalidOperationException("SQLITE_BUSY")));
        Assert.False(IngestionRetryPolicy.IsTransient(new OperationCanceledException()));
    }
}
