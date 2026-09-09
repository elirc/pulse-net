using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

internal sealed class PersonErasureFixture : IAsyncDisposable
{
    private readonly IngestionReliabilityFixture inner = new();
    public Project Project => inner.Project;
    public GovernanceClock Clock => inner.Clock;
    public Guid Actor { get; } = Guid.NewGuid();
    public SuppressionKeyRing Keys { get; } = new(1, new Dictionary<int, byte[]> { [1] = SHA256.HashData("erasure-unit-tests-only"u8) });
    public PulseDbContext Open(params IInterceptor[] interceptors) => inner.Open(interceptors);
    public PersonErasureService Service(PulseDbContext db) => new(db, Keys, Clock);
    public QueueAdmissionService Admission(PulseDbContext db) => new(db, new(), Clock, Keys);
    public CaptureReceiptService Receipts(PulseDbContext db) => new(db, Clock);
    public IngestionProcessor Processor(PulseDbContext db) => new(db, new CaptureService(db, new IdentityService(db), Clock, Keys), new(), Clock);
    public async Task InitializeAsync()
    {
        await inner.InitializeAsync(); await using var db = Open();
        db.ProjectMemberships.Add(new() { ProjectId = Project.Id, UserId = Actor, Role = ProjectRole.Admin }); await db.SaveChangesAsync();
    }
    public async Task<Guid> PersonAsync(string alias = "known")
    {
        await using var db = Open(); await Admission(db).AdmitAsync(Project.Id, [new("view", alias, null, "{}")], true, default);
        await Processor(db).ProcessPendingAsync();
        return (await db.PersonDistinctIds.SingleAsync(m => m.ProjectId == Project.Id && m.DistinctId == alias)).PersonId;
    }
    public async Task<ErasureJob> RunUntilStoppedAsync(Guid jobId)
    {
        for (var step = 0; step < 100; step++)
        {
            await using var db = Open(); var job = await db.ErasureJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            if (job.Status is ErasureJobStatus.Completed or ErasureJobStatus.NeedsReview or ErasureJobStatus.Failed) return job;
            await Service(db).ProcessAsync(jobId, default);
        }
        throw new InvalidOperationException("Fixture erasure did not stop within 100 bounded phases.");
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
