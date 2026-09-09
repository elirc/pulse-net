using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class IngestionTraceTests
{
    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<AnalyticsEvent>().Any(e => e.State == EntityState.Added)) throw new SqliteException("injected contention", 5);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Logs<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Lines.Add(format(state, error));
    }

    [Fact]
    public async Task DurableContext_BatchDedup_AttemptsAndReplayHaveCorrectRelationships_WithoutPayloadMetadata()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener { ShouldListenTo = source => source.Name == IngestionTrace.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Add(activity) };
        ActivitySource.AddActivityListener(listener);
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        string parent; ActivityTraceId captureTrace; var id = Guid.NewGuid();
        var admissionLogs = new Logs<QueueAdmissionService>();
        using (var capture = IngestionTrace.Producer("test.capture"))
        {
            captureTrace = capture.TraceId; capture.AddBaggage("private", "secret-baggage-marker");
            await using var db = f.Open();
            var input = new IncomingEvent("e", "secret-distinct-marker", null, "{\"private\":\"secret-payload-marker\"}", id);
            var outcome = await new QueueAdmissionService(db, new(), f.Clock, logger: admissionLogs).AdmitAsync(f.Project.Id, [input, input], true, default);
            Assert.Equal(1, outcome.Queued); Assert.Equal(1, outcome.Deduplicated);
            parent = (await db.QueuedEvents.AsNoTracking().SingleAsync()).TraceParent!;
            Assert.True(IngestionTrace.TryParse(parent, out var producer)); Assert.Equal(captureTrace, producer.TraceId);
        }
        // New contexts on every attempt simulate the durable boundary; the fifth failure dead-letters valid input.
        for (var i = 1; i <= 5; i++)
        {
            await using var db = f.Open(new FailSave()); await f.Processor(db).ProcessPendingAsync();
            if (i < 5) f.Clock.Now = (await db.QueuedEvents.SingleAsync()).NextAttemptAt!.Value;
        }
        var attempts = stopped.Where(a => a.OperationName == "ingestion.process" && a.TraceId == captureTrace).ToArray();
        Assert.Equal(5, attempts.Length); Assert.Equal(5, attempts.Select(a => a.SpanId).Distinct().Count());
        IngestionTrace.TryParse(parent, out var original);
        Assert.All(attempts, a => Assert.Equal(original.SpanId, a.ParentSpanId));
        ActivityTraceId replayTrace;
        using (var request = new Activity("separate-replay-request").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            replayTrace = request.TraceId; Assert.NotEqual(captureTrace, replayTrace);
            await using var db = f.Open(); var letter = await db.DeadLetterEvents.AsNoTracking().SingleAsync();
            Assert.Equal(parent, letter.TraceParent);
            Assert.Equal(DeadLetterReplayOutcome.Queued, await f.Operations(db).ReplayAsync(f.Project.Id, letter.Id, default));
            var queued = await db.QueuedEvents.AsNoTracking().SingleAsync();
            Assert.Equal(parent, queued.OriginalTraceParent); Assert.Equal(55, queued.TraceParent!.Length);
        }
        var logs = new Logs<IngestionProcessor>();
        await using (var db = f.Open())
            Assert.Equal((1, 0), await new IngestionProcessor(db, new CaptureService(db, new IdentityService(db), f.Clock), new(), f.Clock, logger: logs).ProcessPendingAsync());
        var replay = Assert.Single(stopped, a => a.OperationName == "ingestion.replay" && a.TraceId == replayTrace);
        Assert.Equal(original.TraceId, Assert.Single(replay.Links).Context.TraceId);
        var final = Assert.Single(stopped, a => a.OperationName == "ingestion.process" && a.TraceId == replayTrace);
        Assert.Equal(replay.SpanId, final.ParentSpanId); Assert.Equal(original.SpanId, Assert.Single(final.Links).Context.SpanId);
        var diagnosticText = string.Join("\n", admissionLogs.Lines.Concat(logs.Lines).Concat(stopped.SelectMany(a => a.Tags).Select(t => t.Key + "=" + t.Value)));
        Assert.DoesNotContain("secret-", diagnosticText); Assert.Contains(captureTrace.ToString(), string.Join("\n", admissionLogs.Lines));
        Assert.Contains(replayTrace.ToString(), string.Join("\n", logs.Lines));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00-00000000000000000000000000000000-0000000000000000-00")]
    public async Task UnusableContextDoesNotLoseBusinessWork(string? context)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        await f.Admission(db).AdmitAsync(f.Project.Id, [new("e", "p", null, "{}")], false, default);
        await db.QueuedEvents.ExecuteUpdateAsync(s => s.SetProperty(q => q.TraceParent, context).SetProperty(q => q.OriginalTraceParent, new string('x', 2000)));
        Assert.Equal((1, 0), await f.Processor(db).ProcessPendingAsync()); Assert.Single(await db.Events.ToListAsync());
    }

    [Fact]
    public void DisabledSamplingStillCreatesSafeLocalCorrelation()
    {
        using var listener = new ActivityListener { ShouldListenTo = s => s.Name == IngestionTrace.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None };
        ActivitySource.AddActivityListener(listener);
        using var producer = IngestionTrace.Producer("test.unsampled");
        using var attempt = IngestionTrace.Consumer(IngestionTrace.Format(producer.Context), null);
        Assert.Equal(producer.TraceId, attempt.TraceId); Assert.NotEqual(producer.SpanId, attempt.SpanId);
        Assert.Equal(producer.SpanId, attempt.ParentSpanId); Assert.Null(IngestionTrace.Normalize(new string('a', 500)));
    }
}
