using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

internal sealed class IngestionReliabilityFixture : IAsyncDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"pulse-ingestion-{Guid.NewGuid():N}.db");
    public Project Project { get; } = new() { Name = "Reliability", ApiKey = Guid.NewGuid().ToString(), ReadKey = Guid.NewGuid().ToString() };
    public GovernanceClock Clock { get; } = new();
    public PulseDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PulseDbContext>()
        .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 15 }.ToString()).AddInterceptors(interceptors).Options);
    public QueueAdmissionService Admission(PulseDbContext db) => new(db, new IngestionSignal(), Clock);
    public CaptureReceiptService Receipts(PulseDbContext db) => new(db, Clock);
    public IngestionProcessor Processor(PulseDbContext db, IngestionCounters? counters = null, IngestionWorkerIdentity? owner = null) =>
        new(db, new CaptureService(db, new IdentityService(db), Clock), counters ?? new(), Clock, owner ?? new());
    public IngestionOperationsService Operations(PulseDbContext db) => new(db, new IngestionSignal(), Clock);
    public async Task InitializeAsync()
    {
        await using var db = Open(); await db.Database.MigrateAsync(); db.Projects.Add(Project); await db.SaveChangesAsync();
    }
    public ValueTask DisposeAsync()
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) if (File.Exists(file)) File.Delete(file);
        return ValueTask.CompletedTask;
    }
}
