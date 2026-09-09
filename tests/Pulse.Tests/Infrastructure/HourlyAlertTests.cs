using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public sealed class HourlyAlertTests
{
    [Theory]
    [InlineData("2026-09-08T10:15:00Z", "2026-09-08T11:00:00Z")]
    [InlineData("2026-09-08T10:00:00Z", "2026-09-08T10:00:00Z")]
    public void First_window_starts_on_the_next_hour_boundary(string changedAt, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected),
            HourlyAlertService.FirstEligibleWindowStart(DateTimeOffset.Parse(changedAt)));
    }

    [Fact]
    public async Task Equality_triggers_once_and_hour_end_is_excluded()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        var rule = fixture.Rule(next: "2026-09-08T11:00:00Z", threshold: 2);
        db.Set<AlertRule>().Add(rule);
        db.Events.AddRange(
            fixture.Event("2026-09-08T10:59:59Z"),
            fixture.Event("2026-09-08T11:00:00Z"),
            fixture.Event("2026-09-08T11:59:59Z"),
            fixture.Event("2026-09-08T12:00:00Z"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service(db).ProcessDueAsync());
        var evaluation = await db.Set<AlertEvaluation>().AsNoTracking().SingleAsync();
        var notification = await db.Set<ProjectNotification>().AsNoTracking().SingleAsync();
        Assert.Equal(2, evaluation.ObservedCount);
        Assert.True(evaluation.Triggered);
        Assert.Equal(evaluation.Id, notification.EvaluationId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T12:00:00Z"), evaluation.WindowEnd);

        db.Events.Add(fixture.Event("2026-09-08T11:30:00Z"));
        await db.SaveChangesAsync();
        Assert.Equal(0, await fixture.Service(db).ProcessDueAsync());
        Assert.Equal(1, await db.Set<AlertEvaluation>().CountAsync());
        Assert.Equal(1, await db.Set<ProjectNotification>().CountAsync());
    }

    [Fact]
    public async Task Below_threshold_records_evaluation_without_notification()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        db.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 2));
        db.Events.Add(fixture.Event("2026-09-08T11:20:00Z"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service(db).ProcessDueAsync());
        Assert.False((await db.Set<AlertEvaluation>().SingleAsync()).Triggered);
        Assert.Empty(await db.Set<ProjectNotification>().ToListAsync());
    }

    [Fact]
    public async Task Empty_window_records_zero_count_without_notification()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        db.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service(db).ProcessDueAsync());
        var evaluation = await db.Set<AlertEvaluation>().SingleAsync();
        Assert.Equal(0, evaluation.ObservedCount);
        Assert.False(evaluation.Triggered);
        Assert.Empty(await db.Set<ProjectNotification>().ToListAsync());
    }

    [Fact]
    public async Task Catch_up_skips_old_history_and_processes_at_most_ten_windows()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        var rule = fixture.Rule("2026-09-01T00:00:00Z", threshold: 1);
        db.Set<AlertRule>().Add(rule);
        await db.SaveChangesAsync();

        Assert.Equal(10, await fixture.Service(db).ProcessDueAsync());
        var stored = await db.Set<AlertRule>().AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-09-07T12:00:00Z"), stored.SkippedThrough);
        Assert.Equal(DateTimeOffset.Parse("2026-09-07T22:00:00Z"), stored.NextWindowStart);
        Assert.Equal(10, await db.Set<AlertEvaluation>().CountAsync());
    }

    [Fact]
    public async Task Paused_project_is_not_evaluated()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        db.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 1));
        db.ProjectIngestionStates.Add(new ProjectIngestionState { ProjectId = fixture.ProjectId, Paused = true });
        await db.SaveChangesAsync();

        Assert.Equal(0, await fixture.Service(db).ProcessDueAsync());
        Assert.Empty(await db.Set<AlertEvaluation>().ToListAsync());
    }

    [Fact]
    public async Task Paused_oldest_rule_does_not_block_another_project()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using var db = fixture.Open();
        var otherProject = new Project
        {
            Name = "Still running",
            ApiKey = Guid.NewGuid().ToString(),
            ReadKey = Guid.NewGuid().ToString(),
        };
        db.Projects.Add(otherProject);
        db.Set<AlertRule>().Add(fixture.Rule("2026-09-01T00:00:00Z", threshold: 1));
        db.Set<AlertRule>().Add(new AlertRule
        {
            ProjectId = otherProject.Id,
            Name = "Other project's rule",
            EventName = "signup",
            Threshold = 1,
            Enabled = true,
            NextWindowStart = DateTimeOffset.Parse("2026-09-08T11:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-09-08T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-09-08T10:00:00Z"),
        });
        db.ProjectIngestionStates.Add(new ProjectIngestionState { ProjectId = fixture.ProjectId, Paused = true });
        await db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Service(db).ProcessDueAsync());
        var evaluation = await db.Set<AlertEvaluation>().SingleAsync();
        Assert.Equal(otherProject.Id, evaluation.ProjectId);
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_evidence_notification_and_progress()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        Guid ruleId;
        await using (var seed = fixture.Open())
        {
            var rule = fixture.Rule("2026-09-08T11:00:00Z", threshold: 1);
            ruleId = rule.Id;
            seed.Set<AlertRule>().Add(rule);
            seed.Events.Add(fixture.Event("2026-09-08T11:20:00Z"));
            await seed.SaveChangesAsync();
        }

        await using (var failing = fixture.Open(new FailAlertCommit()))
        {
            await Assert.ThrowsAsync<InjectedAlertFailure>(() => fixture.Service(failing).ProcessDueAsync());
        }
        await using (var verify = fixture.Open())
        {
            Assert.Empty(await verify.Set<AlertEvaluation>().ToListAsync());
            Assert.Empty(await verify.Set<ProjectNotification>().ToListAsync());
            Assert.Equal(DateTimeOffset.Parse("2026-09-08T11:00:00Z"),
                (await verify.Set<AlertRule>().SingleAsync(r => r.Id == ruleId)).NextWindowStart);
            Assert.Equal(1, await fixture.Service(verify).ProcessDueAsync());
            Assert.Single(await verify.Set<AlertEvaluation>().ToListAsync());
            Assert.Single(await verify.Set<ProjectNotification>().ToListAsync());
        }
    }

    [Fact]
    public async Task Restart_after_commit_uses_persisted_progress_without_duplicates()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using (var firstHost = fixture.Open())
        {
            firstHost.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 1));
            firstHost.Events.Add(fixture.Event("2026-09-08T11:20:00Z"));
            await firstHost.SaveChangesAsync();
            Assert.Equal(1, await fixture.Service(firstHost).ProcessDueAsync());
        }

        await using var restarted = fixture.Open();
        Assert.Equal(0, await fixture.Service(restarted).ProcessDueAsync());
        Assert.Equal(1, await restarted.Set<AlertEvaluation>().CountAsync());
        Assert.Equal(1, await restarted.Set<ProjectNotification>().CountAsync());
    }

    [Fact]
    public async Task Two_processors_cannot_commit_duplicate_window_or_notification()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using (var seed = fixture.Open())
        {
            seed.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 1));
            seed.Events.Add(fixture.Event("2026-09-08T11:20:00Z"));
            await seed.SaveChangesAsync();
        }

        var barrier = new TransactionStartBarrier(participants: 2);
        var attempts = await Task.WhenAll(
            Task.Run(async () =>
            {
                await using var left = fixture.Open(barrier);
                return await Record.ExceptionAsync(() => fixture.Service(left).ProcessDueAsync());
            }),
            Task.Run(async () =>
            {
                await using var right = fixture.Open(barrier);
                return await Record.ExceptionAsync(() => fixture.Service(right).ProcessDueAsync());
            }));
        Assert.True(attempts.Count(exception => exception is null) >= 1);

        await using var retry = fixture.Open();
        await fixture.Service(retry).ProcessDueAsync();
        Assert.Equal(1, await retry.Set<AlertEvaluation>().CountAsync());
        Assert.Equal(1, await retry.Set<ProjectNotification>().CountAsync());
    }

    [Fact]
    public async Task Disable_edit_after_selection_prevents_old_revision_evaluation()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        Guid ruleId;
        await using (var seed = fixture.Open())
        {
            var rule = fixture.Rule("2026-09-08T11:00:00Z", threshold: 1);
            ruleId = rule.Id;
            seed.Set<AlertRule>().Add(rule);
            await seed.SaveChangesAsync();
        }

        var gate = new TransactionStartGate();
        var staleProcessor = Task.Run(async () =>
        {
            await using var worker = fixture.Open(gate);
            return await fixture.Service(worker).ProcessDueAsync();
        });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var editor = fixture.Open())
        {
            await editor.Set<AlertRule>().Where(r => r.Id == ruleId).ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Enabled, false)
                .SetProperty(r => r.Revision, 2)
                .SetProperty(r => r.NextWindowStart, DateTimeOffset.Parse("2026-09-08T12:00:00Z")));
        }
        gate.Release.TrySetResult();

        Assert.Equal(0, await staleProcessor);
        await using var verify = fixture.Open();
        Assert.Empty(await verify.Set<AlertEvaluation>().ToListAsync());
    }

    [Fact]
    public async Task Failure_after_commit_then_restart_does_not_duplicate_persisted_work()
    {
        await using var fixture = await AlertFixture.CreateAsync();
        await using (var seed = fixture.Open())
        {
            seed.Set<AlertRule>().Add(fixture.Rule("2026-09-08T11:00:00Z", threshold: 1));
            seed.Events.Add(fixture.Event("2026-09-08T11:20:00Z"));
            await seed.SaveChangesAsync();
        }

        await using (var committing = fixture.Open(new FailAfterCommit()))
        {
            await Assert.ThrowsAsync<InjectedAfterCommitFailure>(() => fixture.Service(committing).ProcessDueAsync());
        }
        await using var restarted = fixture.Open();
        Assert.Equal(0, await fixture.Service(restarted).ProcessDueAsync());
        Assert.Equal(1, await restarted.Set<AlertEvaluation>().CountAsync());
        Assert.Equal(1, await restarted.Set<ProjectNotification>().CountAsync());
    }

    private sealed class AlertFixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"pulse-alerts-{Guid.NewGuid():N}.db");
        public Guid ProjectId { get; } = Guid.NewGuid();
        public GovernanceClock Clock { get; } = new() { Now = DateTimeOffset.Parse("2026-09-08T12:00:00Z") };

        public static async Task<AlertFixture> CreateAsync()
        {
            var fixture = new AlertFixture();
            await using var db = fixture.Open();
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project
            {
                Id = fixture.ProjectId,
                Name = "Alerts",
                ApiKey = Guid.NewGuid().ToString(),
                ReadKey = Guid.NewGuid().ToString(),
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public PulseDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PulseDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = 15,
            }.ToString()).AddInterceptors(interceptors).Options);

        public HourlyAlertService Service(PulseDbContext db) => new(db, Clock);

        public AlertRule Rule(string next, int threshold) => new()
        {
            ProjectId = ProjectId,
            Name = "Hourly signup volume",
            EventName = "signup",
            Threshold = threshold,
            Enabled = true,
            NextWindowStart = DateTimeOffset.Parse(next),
            CreatedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        };

        public AnalyticsEvent Event(string timestamp) => new()
        {
            ProjectId = ProjectId,
            Name = "signup",
            DistinctId = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.Parse(timestamp),
        };

        public ValueTask DisposeAsync()
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedAlertFailure : Exception;
    private sealed class InjectedAfterCommitFailure : Exception;

    private sealed class FailAlertCommit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AlertEvaluation>().Any(e => e.State == EntityState.Added) == true)
            {
                throw new InjectedAlertFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InjectedAfterCommitFailure());
    }

    private sealed class TransactionStartGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class TransactionStartBarrier(int participants) : DbTransactionInterceptor
    {
        private readonly CountdownEvent arrivals = new(participants);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            arrivals.Signal();
            if (arrivals.CurrentCount == 0) release.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
