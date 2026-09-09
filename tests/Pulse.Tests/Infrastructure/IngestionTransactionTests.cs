using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionTransactionTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly PulseDbContext _db;
    private readonly IngestionCounters _counters = new();
    private readonly IngestionProcessor _processor;
    private readonly Project _project = new() { Name = "Transactions", ApiKey = "write", ReadKey = "read" };

    public IngestionTransactionTests()
    {
        _connection.Open();
        _db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Projects.Add(_project);
        _db.SaveChanges();
        _processor = new IngestionProcessor(_db,
            new CaptureService(_db, new IdentityService(_db), TimeProvider.System), _counters);
    }

    [Fact]
    public async Task AcknowledgementFailure_RollsBackEventPersonAndDefinitions_ThenRetrySucceedsOnce()
    {
        await EnqueueAsync(JsonSerializer.Serialize(new IncomingEvent("purchase", "u1", null, "{\"plan\":\"pro\"}")));
        await BlockAcknowledgementAsync();

        // A trigger constraint failure is an unclassified storage fault, not
        // recognized SQLite contention. Abort without consuming a retry attempt.
        await Assert.ThrowsAsync<SqliteException>(() => _processor.ProcessPendingAsync());
        Assert.Empty(await _db.Events.ToListAsync());
        Assert.Empty(await _db.Persons.ToListAsync());
        Assert.Empty(await _db.PersonDistinctIds.ToListAsync());
        Assert.Empty(await _db.EventDefinitions.ToListAsync());
        Assert.Empty(await _db.PropertyDefinitions.ToListAsync());
        Assert.Equal(0, (await _db.QueuedEvents.AsNoTracking().SingleAsync()).Attempts);
        Assert.Equal(0, _counters.Processed);

        await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER block_ack");
        Assert.Equal((1, 0), await _processor.ProcessPendingAsync());
        Assert.Equal((0, 0), await _processor.ProcessPendingAsync());
        Assert.Single(await _db.Events.ToListAsync());
        Assert.Empty(await _db.QueuedEvents.ToListAsync());
        Assert.Equal(1, _counters.Processed);
    }

    [Fact]
    public async Task DeadLetterAcknowledgementFailure_RetainsQueueAndRollsBackLetter()
    {
        await EnqueueAsync("broken json");
        await BlockAcknowledgementAsync();

        await Assert.ThrowsAsync<SqliteException>(() => _processor.ProcessPendingAsync());
        Assert.Single(await _db.QueuedEvents.ToListAsync());
        Assert.Empty(await _db.DeadLetterEvents.ToListAsync());
        Assert.Equal(0, _counters.DeadLettered);

        _db.ChangeTracker.Clear(); // A new worker cycle also gets a fresh scope.
        await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER block_ack");
        Assert.Equal((0, 1), await _processor.ProcessPendingAsync());
        Assert.Single(await _db.DeadLetterEvents.ToListAsync());
        Assert.Empty(await _db.QueuedEvents.ToListAsync());
    }

    [Fact]
    public async Task ReplayEnqueueFailure_RetainsDeadLetter()
    {
        var letter = new DeadLetterEvent
        {
            ProjectId = _project.Id, Error = "Temporary outage",
            PayloadJson = JsonSerializer.Serialize(new IncomingEvent("purchase", "u1", null, "{}")),
        };
        _db.DeadLetterEvents.Add(letter);
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER block_enqueue BEFORE INSERT ON QueuedEvents
            BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;
            """);
        var service = new IngestionOperationsService(_db, new IngestionSignal(), TimeProvider.System);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReplayAsync(_project.Id, letter.Id, default));
        Assert.Single(await _db.DeadLetterEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.QueuedEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Replay_PreservesPayload_ResetsBudget_AndUsesFreshEnqueueTime()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var payload = JsonSerializer.Serialize(new IncomingEvent("purchase", "u1", now.AddDays(-3), "{}"));
        var letter = new DeadLetterEvent
        {
            ProjectId = _project.Id, Error = "Temporary outage", Attempts = 2,
            PayloadJson = payload, FailedAt = now.AddDays(-1),
        };
        _db.DeadLetterEvents.Add(letter);
        await _db.SaveChangesAsync();
        var service = new IngestionOperationsService(_db, new IngestionSignal(), new FixedClock(now));

        Assert.Equal(DeadLetterReplayOutcome.Queued, await service.ReplayAsync(_project.Id, letter.Id, default));
        var queued = await _db.QueuedEvents.AsNoTracking().SingleAsync();
        Assert.Equal(payload, queued.PayloadJson);
        Assert.Equal(0, queued.Attempts);
        Assert.Equal(now, queued.EnqueuedAt);
        Assert.Empty(await _db.DeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task Metrics_UseProjectScopeAndInjectedClock_AndEmptyQueueHasNoAge()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var service = new IngestionOperationsService(_db, new IngestionSignal(), new FixedClock(now));
        var empty = await service.GetMetricsAsync(_project.Id, default);
        Assert.Equal(new ProjectIngestionMetrics(0, 0, null, null), empty);

        await EnqueueAsync("{}", now.AddSeconds(-90));
        await EnqueueAsync("{}", now.AddSeconds(-5));
        _db.QueuedEvents.Add(new QueuedEvent { ProjectId = Guid.NewGuid(), PayloadJson = "{}", EnqueuedAt = now.AddDays(-2) });
        await _db.SaveChangesAsync();

        var metrics = await service.GetMetricsAsync(_project.Id, default);
        Assert.Equal(2, metrics.Pending);
        Assert.Equal(now.AddSeconds(-90), metrics.OldestEnqueuedAt);
        Assert.Equal(90d, metrics.OldestPendingAgeSeconds);
    }

    [Fact]
    public async Task Metrics_ClampFutureQueueTimestampToZeroAge()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await EnqueueAsync("{}", now.AddMinutes(1));
        var service = new IngestionOperationsService(_db, new IngestionSignal(), new FixedClock(now));
        Assert.Equal(0d, (await service.GetMetricsAsync(_project.Id, default)).OldestPendingAgeSeconds);
    }

    private async Task EnqueueAsync(string payload, DateTimeOffset? enqueuedAt = null)
    {
        _db.QueuedEvents.Add(new QueuedEvent
        {
            ProjectId = _project.Id, PayloadJson = payload, EnqueuedAt = enqueuedAt ?? DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private Task BlockAcknowledgementAsync() => _db.Database.ExecuteSqlRawAsync("""
        CREATE TRIGGER block_ack BEFORE DELETE ON QueuedEvents
        BEGIN SELECT RAISE(ABORT, 'simulated acknowledgement failure'); END;
        """);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
