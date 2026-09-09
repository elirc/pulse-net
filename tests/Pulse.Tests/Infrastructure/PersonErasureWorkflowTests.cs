using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Auth;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class PersonErasureWorkflowTests
{
    [Fact]
    public async Task ReadSnapshotFinishesBeforeErasurePauseCanCommit_AndFollowingReadSeesPause()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync();
        var snapshotEstablished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionStarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var readerDb = f.Open();
        var services = new ServiceCollection().AddSingleton(readerDb).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = HttpMethods.Get;
        http.SetEndpoint(new RouteEndpointBuilder(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/projects/{projectId:guid}/persons"), 0).Build());
        var invocation = new DefaultEndpointFilterInvocationContext(http);
        var filter = new ProjectReadSnapshotFilter();
        var read = filter.InvokeAsync(invocation, async _ =>
        {
            Assert.True(await readerDb.Persons.AnyAsync(p => p.ProjectId == f.Project.Id && p.Id == person));
            snapshotEstablished.TrySetResult();
            await releaseSnapshot.Task;
            Assert.True(await readerDb.Persons.AnyAsync(p => p.ProjectId == f.Project.Id && p.Id == person));
            return Results.Ok();
        }).AsTask();

        await snapshotEstablished.Task;
        await using var writer = f.Open(new TransactionStartingInterceptor(transactionStarting));
        var initiation = Task.Run(() => f.Service(writer).InitiateAsync(f.Project.Id, person, f.Actor, default));
        var startSignal = await Task.WhenAny(transactionStarting.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        var completedBeforeRelease = initiation.IsCompleted;
        try
        {
            Assert.Same(transactionStarting.Task, startSignal);
            Assert.False(completedBeforeRelease);
        }
        finally { releaseSnapshot.TrySetResult(); }
        await read;
        Assert.Equal(202, (await initiation).Status);

        await using var verify = f.Open();
        Assert.True((await verify.ProjectIngestionStates.AsNoTracking().SingleAsync(s => s.ProjectId == f.Project.Id)).Paused);
    }

    [Fact]
    public async Task InitiationFailure_RollsBackPauseJobAndSuppressionTogether()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync(); await using var db = f.Open();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_erasure_start BEFORE INSERT ON ErasureJobs BEGIN SELECT RAISE(ABORT, 'injected initiation failure'); END;");

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default));
        Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.ErasureJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ErasureIdentities.AsNoTracking().ToListAsync());
        Assert.Empty(await db.IdentitySuppressions.AsNoTracking().ToListAsync());
        var state = await db.ProjectIngestionStates.AsNoTracking().SingleAsync(s => s.ProjectId == f.Project.Id);
        Assert.False(state.Paused);
        Assert.Null(state.ErasureJobId);
        Assert.Equal(0, state.MaintenanceGeneration);
    }

    [Fact]
    public async Task EventBatchBoundary_RestartsAfterOneThousandRowsWithoutSkippingTheLastRow()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync();
        await using (var seed = f.Open())
        {
            seed.Events.AddRange(Enumerable.Range(0, 1000).Select(i => new AnalyticsEvent
            {
                ProjectId = f.Project.Id,
                PersonId = person,
                Name = "bulk-" + i,
                DistinctId = "known"
            }));
            await seed.SaveChangesAsync();
        }

        Guid jobId;
        await using (var start = f.Open()) jobId = (await f.Service(start).InitiateAsync(f.Project.Id, person, f.Actor, default)).Job!.Id;
        var completed = await f.RunUntilStoppedAsync(jobId);

        Assert.Equal(ErasureJobStatus.Completed, completed.Status);
        Assert.Equal(1001, completed.RemovedEvents);
        await using var verify = f.Open();
        Assert.False(await verify.Events.AnyAsync(e => e.ProjectId == f.Project.Id && e.PersonId == person));
    }

    [Fact]
    public async Task CompletedErasure_BlocksRetryBeforeExistingClientUuidCanDeduplicate()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); await using var db = f.Open();
        var clientEventId = Guid.NewGuid();
        var request = new IncomingEvent("view", "known", null, "{}", clientEventId);
        Assert.Equal(202, (await f.Admission(db).AdmitAsync(f.Project.Id, [request], true, default)).Status);
        Assert.Equal(1, (await f.Processor(db).ProcessPendingAsync()).Processed);
        var person = (await db.PersonDistinctIds.SingleAsync(m => m.ProjectId == f.Project.Id && m.DistinctId == "known")).PersonId;
        var job = (await f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default)).Job!;
        Assert.Equal(ErasureJobStatus.Completed, (await f.RunUntilStoppedAsync(job.Id)).Status);

        db.ChangeTracker.Clear();
        var retry = await f.Admission(db).AdmitAsync(f.Project.Id, [request], true, default);
        Assert.Equal(422, retry.Status);
        Assert.Equal("identity_suppressed", retry.Code);
        Assert.Empty(await db.QueuedEvents.Where(q => q.ProjectId == f.Project.Id).ToListAsync());
    }

    [Fact]
    public async Task CompletedErasure_RejectsStaleLeaseAndSuppressesAReintroducedDeadLetterReplay()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); await using var db = f.Open();
        await f.Admission(db).AdmitAsync(f.Project.Id, [new("view", "known", null, "{}")], true, default);
        var lease = (await new IngestionLeaseService(db, f.Clock).ClaimAsync(f.Project.Id, Guid.NewGuid(), default))!;
        var staleRow = await db.QueuedEvents.AsNoTracking().SingleAsync();
        var personRow = new Person { ProjectId = f.Project.Id };
        db.Persons.Add(personRow);
        db.PersonDistinctIds.Add(new() { ProjectId = f.Project.Id, PersonId = personRow.Id, DistinctId = "known" });
        await db.SaveChangesAsync();
        var job = (await f.Service(db).InitiateAsync(f.Project.Id, personRow.Id, f.Actor, default)).Job!;
        Assert.Equal(ErasureJobStatus.Completed, (await f.RunUntilStoppedAsync(job.Id)).Status);

        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<LostIngestionLeaseException>(() => f.Processor(db).ProcessLeasedRowAsync(staleRow, f.Project, lease, default));
        var replayPayload = JsonSerializer.Serialize(new IncomingEvent("late-replay", "known", null, "{}"));
        var letter = new DeadLetterEvent { ProjectId = f.Project.Id, PayloadJson = replayPayload, Error = "delayed external copy" };
        db.DeadLetterEvents.Add(letter); await db.SaveChangesAsync();
        var replay = await new IngestionOperationsService(db, new(), f.Clock, f.Keys).ReplayAsync(f.Project.Id, letter.Id, default);
        Assert.Equal(DeadLetterReplayOutcome.Suppressed, replay);
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letter.Id));
        Assert.False(await db.QueuedEvents.AnyAsync(q => q.ProjectId == f.Project.Id));
    }

    [Fact]
    public async Task RestartAtEveryPhase_ErasesKnownAliasesAndCopies_PreservesUnrelatedDataAndRejectsRecreation()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); await using var db = f.Open();
        await f.Admission(db).AdmitAsync(f.Project.Id, [new("view", "anon-secret", null, "{}"), new("$identify", "known-secret", null, "{\"$anon_distinct_id\":\"anon-secret\"}"), new("view", "survivor", null, "{}")], true, default);
        Assert.Equal(3, (await f.Processor(db).ProcessPendingAsync()).Processed);
        var target = (await db.PersonDistinctIds.SingleAsync(m => m.DistinctId == "known-secret")).PersonId;
        var survivor = (await db.PersonDistinctIds.SingleAsync(m => m.DistinctId == "survivor")).PersonId;
        var originalReceipt = await db.CaptureReceipts.SingleAsync();
        var legacy = new AnalyticsEvent { ProjectId = f.Project.Id, Name = "legacy", DistinctId = "anon-secret", PersonId = null };
        var cohort = new Cohort { ProjectId = f.Project.Id, Name = "Audience", Type = CohortType.Static };
        db.Events.Add(legacy); db.Cohorts.Add(cohort); db.CohortPersons.AddRange(new CohortPerson { CohortId = cohort.Id, PersonId = target }, new CohortPerson { CohortId = cohort.Id, PersonId = survivor });
        var completed = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Completed, ResultContent = "private snapshot values", SnapshotReady = true };
        var running = new ExportJob { ProjectId = f.Project.Id, Type = "events", Format = "json" };
        var foreign = new ExportJob { ProjectId = Guid.NewGuid(), Type = "events", Format = "json", Status = ExportJobStatus.Completed, ResultContent = "foreign keep" };
        db.ExportJobs.AddRange(completed, running, foreign); db.ExportSnapshotRows.Add(new() { ProjectId = f.Project.Id, JobId = completed.Id, Ordinal = 0, RowJson = "private snapshot values" }); await db.SaveChangesAsync();
        var owner = new ExportOwnershipService(db, f.Clock); var oldAttempt = (await owner.ClaimAsync(f.Project.Id, running.Id, Guid.NewGuid(), default))!;
        await f.Admission(db).AdmitAsync(f.Project.Id, [new("late", "known-secret", null, "{}"), new("$identify", "new-alias", null, "{\"$anon_distinct_id\":\" anon-secret \"}"), new("late", "survivor", null, "{}")], true, default);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var queued = await db.QueuedEvents.OrderBy(q => q.Seq).FirstAsync();
            var letter = new DeadLetterEvent { ProjectId = f.Project.Id, AdmissionId = queued.AdmissionId, PayloadJson = queued.PayloadJson, Error = "fixture failure", Attempts = 5 };
            db.DeadLetterEvents.Add(letter); await f.Receipts(db).TransitionAsync(f.Project.Id, queued.AdmissionId, ProcessingItemState.DeadLettered, null, letter.Id, default);
            await db.QueuedEvents.Where(q => q.Seq == queued.Seq).ExecuteDeleteAsync(); await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var initiated = await f.Service(db).InitiateAsync(f.Project.Id, target, f.Actor, default); Assert.Equal(202, initiated.Status); var jobId = initiated.Job!.Id;
        Assert.True((await db.ProjectIngestionStates.AsNoTracking().SingleAsync()).Paused);
        Assert.Equal(503, (await f.Admission(db).AdmitAsync(f.Project.Id, [new("wait", "survivor", null, "{}")], false, default)).Status);
        Assert.Equal(202, (await f.Service(db).InitiateAsync(f.Project.Id, target, f.Actor, default)).Status);
        Assert.False(await owner.PublishAsync(oldAttempt, "stale private data", "application/json", 1, null, default));
        var job = await f.RunUntilStoppedAsync(jobId); Assert.Equal(ErasureJobStatus.Completed, job.Status); Assert.Equal(3, job.RemovedEvents); Assert.Equal(2, job.InvalidatedExports);
        db.ChangeTracker.Clear(); Assert.False((await db.ProjectIngestionStates.AsNoTracking().SingleAsync()).Paused);
        Assert.False(await db.Persons.AnyAsync(p => p.Id == target)); Assert.True(await db.Persons.AnyAsync(p => p.Id == survivor));
        Assert.All(await db.Events.Where(e => e.ProjectId == f.Project.Id).ToListAsync(), e => Assert.Equal("survivor", e.DistinctId));
        Assert.Single(await db.CohortPersons.ToListAsync()); Assert.Empty(await db.DeadLetterEvents.ToListAsync()); Assert.Single(await db.QueuedEvents.ToListAsync());
        Assert.Empty(await db.ExportSnapshotRows.ToListAsync()); Assert.Equal("foreign keep", (await db.ExportJobs.AsNoTracking().SingleAsync(j => j.Id == foreign.Id)).ResultContent);
        Assert.All(await db.ExportJobs.AsNoTracking().Where(j => j.ProjectId == f.Project.Id).ToListAsync(), e => { Assert.Null(e.ResultContent); Assert.Equal(ExportJobStatus.Cancelled, e.Status); Assert.Equal(jobId, e.InvalidatedByErasureJobId); });
        var receipt = (await f.Receipts(db).ReadAsync(f.Project.Id, originalReceipt.Id, default))!;
        Assert.Equal(3, receipt.Processed); Assert.Equal(2, receipt.Items.Count(i => i.RetiredReason == "erasure" && i.EventId == null));
        Assert.Equal(2, await db.CaptureProcessingItems.CountAsync(i => i.State == ProcessingItemState.Suppressed));
        Assert.All(await db.IdentitySuppressions.ToListAsync(), row => { Assert.Equal(64, row.Fingerprint.Length); Assert.DoesNotContain("secret", JsonSerializer.Serialize(row)); });
        foreach (var incoming in new[] { new IncomingEvent("retry", "known-secret", null, "{}"), new("$identify", "unknown", null, "{\"$anon_distinct_id\":\"anon-secret\"}") })
            Assert.Equal(422, (await f.Admission(db).AdmitAsync(f.Project.Id, [incoming], true, default)).Status);
        Assert.False(await owner.PublishAsync(oldAttempt, "late after completion", "application/json", 1, null, default));
        // An identity never known to the project cannot be inferred to be the same person.
        Assert.Equal(202, (await f.Admission(db).AdmitAsync(f.Project.Id, [new("new", "never-known", null, "{}")], false, default)).Status);
        Assert.Equal(2, (await f.Processor(db).ProcessPendingAsync()).Processed);
    }

    [Fact]
    public async Task UnreadableEnvelopes_KeepPauseUntilExplicitHashCheckedDiscard_AndNeverExposePayloadInReport()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync(); await using var db = f.Open();
        var queue = new QueuedEvent { ProjectId = f.Project.Id, PayloadJson = "private-unreadable-marker" };
        var letter = new DeadLetterEvent { ProjectId = f.Project.Id, PayloadJson = "another-private-marker", Error = "fixture" };
        db.QueuedEvents.Add(queue); db.DeadLetterEvents.Add(letter); await db.SaveChangesAsync();
        var initiated = await f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default); var id = initiated.Job!.Id;
        var job = await f.RunUntilStoppedAsync(id); Assert.Equal(ErasureJobStatus.NeedsReview, job.Status);
        db.ChangeTracker.Clear(); var report = await db.ErasureUnreadableItems.SingleAsync();
        Assert.DoesNotContain("private-unreadable-marker", JsonSerializer.Serialize(report)); Assert.Null(report.DiscardedAt);
        await db.ProjectIngestionStates.Where(s => s.ProjectId == f.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Paused, false).SetProperty(x => x.ErasureJobId, (Guid?)null));
        var selection = new ErasureDiscardSelection(queue.Seq, null, report.ContentHash);
        var lostOwnership = await f.Service(db).DiscardUnreadableAsync(f.Project.Id, id, f.Actor, [selection], default);
        Assert.Equal(409, lostOwnership.Status); Assert.Equal("erasure_maintenance_ownership_changed", lostOwnership.Code);
        Assert.Single(await db.QueuedEvents.ToListAsync());
        await db.ProjectIngestionStates.Where(s => s.ProjectId == f.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Paused, true).SetProperty(x => x.ErasureJobId, id));
        await db.QueuedEvents.Where(q => q.Seq == queue.Seq).ExecuteUpdateAsync(s => s.SetProperty(q => q.PayloadJson, "changed"));
        Assert.Equal(409, (await f.Service(db).DiscardUnreadableAsync(f.Project.Id, id, f.Actor, [selection], default)).Status);
        Assert.Single(await db.QueuedEvents.ToListAsync()); Assert.True((await db.ProjectIngestionStates.AsNoTracking().SingleAsync()).Paused);
        await db.QueuedEvents.Where(q => q.Seq == queue.Seq).ExecuteUpdateAsync(s => s.SetProperty(q => q.PayloadJson, queue.PayloadJson));
        Assert.Equal(202, (await f.Service(db).DiscardUnreadableAsync(f.Project.Id, id, f.Actor, [selection], default)).Status);
        job = await f.RunUntilStoppedAsync(id); Assert.Equal(ErasureJobStatus.NeedsReview, job.Status);
        db.ChangeTracker.Clear(); report = await db.ErasureUnreadableItems.SingleAsync(i => i.DiscardedAt == null);
        Assert.Equal(202, (await f.Service(db).DiscardUnreadableAsync(f.Project.Id, id, f.Actor, [new(null, letter.Id, report.ContentHash)], default)).Status);
        job = await f.RunUntilStoppedAsync(id); Assert.Equal(ErasureJobStatus.Completed, job.Status);
        Assert.All(await db.ErasureUnreadableItems.AsNoTracking().ToListAsync(), item => { Assert.Equal(f.Actor, item.DiscardedBy); Assert.NotNull(item.DiscardedAt); });
    }

    [Fact]
    public async Task FailedBatch_RollsBackPointersAndData_KeepsPauseAndResumesFromRecordedPhase()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync(); await using var db = f.Open();
        var created = await f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default); var id = created.Job!.Id;
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_erasure_delete BEFORE DELETE ON Events BEGIN SELECT RAISE(ABORT, 'injected erasure failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.RunUntilStoppedAsync(id));
        db.ChangeTracker.Clear(); var job = await db.ErasureJobs.AsNoTracking().SingleAsync();
        Assert.Equal(ErasureJobStatus.Failed, job.Status); Assert.Equal(ErasurePhase.Events, job.Phase); Assert.Equal(0, job.RemovedEvents);
        Assert.Single(await db.Events.ToListAsync()); Assert.NotNull((await db.CaptureProcessingItems.SingleAsync()).EventId);
        Assert.True((await db.ProjectIngestionStates.AsNoTracking().SingleAsync()).Paused);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_erasure_delete;");
        Assert.Equal(202, (await f.Service(db).ResumeAsync(f.Project.Id, id, f.Actor, default)).Status);
        Assert.Equal(ErasureJobStatus.Completed, (await f.RunUntilStoppedAsync(id)).Status);
    }

    [Fact]
    public async Task MissingKeyVersion_FailsClosed_AndOldRequestGenerationCannotWriteAfterCompletion()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync(); await using var old = f.Open();
        old.GuardedProjectId = f.Project.Id; old.GuardedMaintenanceGeneration = 0;
        var project = await old.Projects.SingleAsync(); project.Name = "stale edit";
        await using var db = f.Open(); var job = (await f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default)).Job!;
        await Assert.ThrowsAsync<MissingSuppressionKeyException>(() => SuppressionKeyRing.Unconfigured.VerifyRequiredVersionsAsync(db, default));
        await Assert.ThrowsAsync<MissingSuppressionKeyException>(() => new IdentitySuppressionService(db, SuppressionKeyRing.Unconfigured)
            .MatchesAnyAsync(f.Project.Id, [new("e", "known", null, "{}")], default));
        Assert.Equal(ErasureJobStatus.Completed, (await f.RunUntilStoppedAsync(job.Id)).Status);
        await Assert.ThrowsAsync<ProjectMaintenanceException>(() => old.SaveChangesAsync());
        Assert.Equal(f.Project.Name, (await db.Projects.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task UnreadableDiscard_ValidatesEverySelectionBeforeDeletingAny()
    {
        await using var f = new PersonErasureFixture(); await f.InitializeAsync(); var person = await f.PersonAsync(); await using var db = f.Open();
        var first = new QueuedEvent { ProjectId = f.Project.Id, PayloadJson = "unreadable-one" };
        var second = new QueuedEvent { ProjectId = f.Project.Id, PayloadJson = "unreadable-two" };
        db.QueuedEvents.AddRange(first, second); await db.SaveChangesAsync();
        var job = (await f.Service(db).InitiateAsync(f.Project.Id, person, f.Actor, default)).Job!;
        Assert.Equal(ErasureJobStatus.NeedsReview, (await f.RunUntilStoppedAsync(job.Id)).Status);
        db.ChangeTracker.Clear();
        var reports = await db.ErasureUnreadableItems.AsNoTracking().OrderBy(i => i.QueueSequence).ToListAsync();
        Assert.Equal(2, reports.Count);
        var result = await f.Service(db).DiscardUnreadableAsync(f.Project.Id, job.Id, f.Actor,
            [new(reports[0].QueueSequence, null, reports[0].ContentHash), new(reports[1].QueueSequence, null, new string('0', 64))], default);
        Assert.Equal(409, result.Status);
        Assert.Equal(2, await db.QueuedEvents.CountAsync(q => q.ProjectId == f.Project.Id));
        Assert.All(await db.ErasureUnreadableItems.AsNoTracking().ToListAsync(), item => Assert.Null(item.DiscardedAt));
    }

    private sealed class TransactionStartingInterceptor(TaskCompletionSource signal) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            signal.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
