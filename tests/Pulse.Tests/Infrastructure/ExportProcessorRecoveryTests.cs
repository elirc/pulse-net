using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ExportProcessorRecoveryTests
{
    private static ServiceProvider Services(IngestionReliabilityFixture f, params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => f.Open(interceptors)); services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton<ExportWorkerIdentity>(); services.AddScoped<ExportService>();
        services.AddScoped<CohortService>(); services.AddScoped<QueryService>(); services.AddScoped<InsightRunnerService>(); services.AddScoped<ExportJobProcessor>();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReachesActiveQuery_AndShutdownLeavesRecoverableRunningWork(bool shutdown)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync();
        Guid id;
        await using (var setup = f.Open()) { var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json" }; id = job.Id; setup.ExportJobs.Add(job); await setup.SaveChangesAsync(); }
        var barrier = new BlockEventQuery();
        await using var provider = Services(f, barrier); using var scope = provider.CreateScope();
        using var host = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var running = Task.Run(() => scope.ServiceProvider.GetRequiredService<ExportJobProcessor>().ProcessPendingAsync(host.Token));
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(90));
        if (shutdown) host.Cancel();
        else
        {
            await using var cancel = f.Open();
            Assert.Equal(202, (await new ExportOwnershipService(cancel, f.Clock).CancelAsync(f.Project.Id, id, default)).Status);
        }
        await barrier.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(90));
        if (shutdown) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(90)));
        else Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(90)));
        await using var verify = f.Open(); var saved = await verify.ExportJobs.AsNoTracking().SingleAsync();
        Assert.Null(saved.Error); Assert.Null(saved.ResultContent);
        Assert.Equal(shutdown ? ExportJobStatus.Running : ExportJobStatus.Cancelled, saved.Status);
        if (shutdown)
        {
            f.Clock.Advance(ExportOwnershipService.LeaseDuration);
            await using var replacement = Services(f); using var next = replacement.CreateScope();
            Assert.Equal(1, await next.ServiceProvider.GetRequiredService<ExportJobProcessor>().ProcessPendingAsync());
            saved = await verify.ExportJobs.AsNoTracking().SingleAsync();
            Assert.Equal(ExportJobStatus.Completed, saved.Status); Assert.Equal(2, saved.AttemptGeneration);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ProcessorRecovery_RendersSameCopiedBytesAfterSourceChanges(string format)
    {
        await using var f = new IngestionReliabilityFixture(); await f.InitializeAsync(); await using var db = f.Open();
        var at = f.Clock.GetUtcNow(); var job = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = format, Consistency = "snapshot",
            ParamsJson = JsonSerializer.Serialize(new { from = at.ToString("O"), to = at.ToString("O") }) };
        db.ExportJobs.Add(job); db.Events.Add(new() { ProjectId = f.Project.Id, Name = "comma,event", DistinctId = "p", Timestamp = at }); await db.SaveChangesAsync();
        var ownership = new ExportOwnershipService(db, f.Clock); var attempt = (await ownership.ClaimAsync(f.Project.Id, job.Id, Guid.NewGuid(), default))!;
        var snapshots = new ExportSnapshotService(db, ownership, f.Clock); await snapshots.CaptureAsync(attempt, null, at, at, default);
        var rows = await snapshots.ReadPageAsync(attempt, 0, default);
        var expected = format == "csv" ? ExportService.EventsCsv(rows) : JsonSerializer.Serialize(new { events = rows }, JsonSerializerOptions.Web);
        await db.Events.ExecuteDeleteAsync(); f.Clock.Advance(ExportOwnershipService.LeaseDuration);
        await using var provider = Services(f); using var scope = provider.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ExportJobProcessor>().ProcessPendingAsync());
        var saved = await db.ExportJobs.AsNoTracking().SingleAsync(); Assert.Equal(expected, saved.ResultContent); Assert.Equal(at, saved.SnapshotCapturedAt);
        Assert.Equal(1, saved.RowCount); Assert.Equal(2, saved.AttemptGeneration);
    }

    private sealed class BlockEventQuery : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Events\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return result;
        }
    }
}
