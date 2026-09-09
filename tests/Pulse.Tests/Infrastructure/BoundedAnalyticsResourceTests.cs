using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;
using Xunit.Abstractions;

namespace Pulse.Tests.Infrastructure;

public class BoundedAnalyticsResourceTests
{
    private readonly ITestOutputHelper _output;
    public BoundedAnalyticsResourceTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public async Task ServerDeadlineAndClientCancellationRemainDistinct()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = Context(connection)) { await setup.Database.EnsureCreatedAsync(); }

        var deadlineGate = new BlockingReaderInterceptor();
        await using (var db = Context(connection, deadlineGate))
        {
            var service = new BoundedQueryService(db);
            var query = service.TrendAsync(Guid.NewGuid(), "view", At(0), At(1), TrendInterval.Hour, [], default);
            await deadlineGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<QueryDeadlineExceededException>(() => query);
        }

        var clientGate = new BlockingReaderInterceptor();
        await using (var db = Context(connection, clientGate))
        {
            var service = new BoundedQueryService(db);
            using var client = new CancellationTokenSource();
            var query = service.TrendAsync(Guid.NewGuid(), "view", At(0), At(1), TrendInterval.Hour, [], client.Token);
            await clientGate.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            client.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        }
    }

    [Fact]
    public async Task RehearsalComparesBoundedAndLegacyProcessAllocationsAtSeveralSizes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var project = Guid.NewGuid();
        var measurements = new List<(int Rows, long BoundedBytes, long BoundedMs, long LegacyBytes, long LegacyMs)>();

        foreach (var size in new[] { 10, 100, 1_000 })
        {
            await db.Events.ExecuteDeleteAsync();
            db.Events.AddRange(Enumerable.Range(0, size).Select(i => new AnalyticsEvent
            {
                ProjectId = project, Name = "view", DistinctId = $"d{i}", PersonId = Guid.NewGuid(),
                Timestamp = At(0).AddSeconds(i), PropertiesJson = "{\"kind\":\"measurement\"}",
            }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var bounded = new BoundedQueryService(db);
            var legacy = new QueryService(db, new CohortService(db, TimeProvider.System));
            // Warm translation/materialization paths before recording either shape.
            await bounded.TrendAsync(project, "view", At(0), At(1), TrendInterval.Hour, [], default);
            await legacy.TrendAsync(project, "view", At(0), At(1), TrendInterval.Hour, [], default);

            var (result, boundedBytes, boundedMs) = await MeasureAsync(() =>
                bounded.TrendAsync(project, "view", At(0), At(1), TrendInterval.Hour, [], default));
            var (_, legacyBytes, legacyMs) = await MeasureAsync(() =>
                legacy.TrendAsync(project, "view", At(0), At(1), TrendInterval.Hour, [], default));
            measurements.Add((size, boundedBytes, boundedMs, legacyBytes, legacyMs));
            Assert.Equal(size, result.Buckets.Sum(b => b.Count));
        }

        foreach (var measurement in measurements)
            _output.WriteLine($"rows={measurement.Rows} boundedAllocatedBytes={measurement.BoundedBytes} boundedElapsedMs={measurement.BoundedMs} legacyAllocatedBytes={measurement.LegacyBytes} legacyElapsedMs={measurement.LegacyMs}");
        _output.WriteLine("Allocation values are process-wide deltas and can include concurrent test/runtime work; use them as rehearsal observations, not stable thresholds or retained-memory measurements.");
        Assert.All(measurements, m => Assert.True(m.BoundedBytes > 0 && m.LegacyBytes > 0));
    }

    private static PulseDbContext Context(SqliteConnection connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);

    private static DateTimeOffset At(int hours) => DateTimeOffset.Parse("2026-05-01T00:00:00Z").AddHours(hours);

    private static async Task<(TrendResult Result, long Bytes, long Milliseconds)> MeasureAsync(Func<Task<TrendResult>> action)
    {
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var result = await action();
        clock.Stop();
        return (result, GC.GetTotalAllocatedBytes(precise: true) - before, clock.ElapsedMilliseconds);
    }

    private sealed class BlockingReaderInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }
}
