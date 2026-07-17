using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;

namespace Pulse.Tests.Infrastructure;

/// <summary>
/// SQLite cannot order or compare DateTimeOffset natively; these tests prove
/// the UTC-ticks value converter keeps round-tripping, ordering and range
/// filtering correct at the database level.
/// </summary>
public class DateTimeOffsetConversionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PulseDbContext _db;

    public DateTimeOffsetConversionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<PulseDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new PulseDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Timestamp_RoundTrips_NormalizedToUtc()
    {
        var original = new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.FromHours(8));
        var project = SeedProject();
        _db.Events.Add(NewEvent(project.Id, "pageview", original));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var loaded = _db.Events.Single();

        Assert.Equal(original.UtcTicks, loaded.Timestamp.UtcTicks);
        Assert.Equal(TimeSpan.Zero, loaded.Timestamp.Offset);
    }

    [Fact]
    public void OrderBy_Timestamp_SortsChronologicallyInSql()
    {
        var project = SeedProject();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _db.Events.AddRange(
            NewEvent(project.Id, "b", t0.AddDays(2)),
            NewEvent(project.Id, "c", t0.AddDays(9)),
            NewEvent(project.Id, "a", t0));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var names = _db.Events.OrderBy(e => e.Timestamp).Select(e => e.Name).ToList();

        Assert.Equal(["a", "b", "c"], names);
    }

    [Fact]
    public void RangeFilter_OnTimestamp_TranslatesCorrectly()
    {
        var project = SeedProject();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _db.Events.AddRange(
            NewEvent(project.Id, "before", t0.AddDays(-1)),
            NewEvent(project.Id, "inside", t0.AddDays(1)),
            NewEvent(project.Id, "after", t0.AddDays(31)));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        var inRange = _db.Events
            .Where(e => e.Timestamp >= t0 && e.Timestamp < t0.AddDays(30))
            .Select(e => e.Name)
            .ToList();

        Assert.Equal(["inside"], inRange);
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Name = "Test",
            ApiKey = Pulse.Domain.ApiKeyGenerator.NewKey(),
            ReadKey = Pulse.Domain.ApiKeyGenerator.NewReadKey(),
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private static AnalyticsEvent NewEvent(Guid projectId, string name, DateTimeOffset timestamp) =>
        new()
        {
            ProjectId = projectId,
            Name = name,
            DistinctId = "user-1",
            Timestamp = timestamp,
        };
}
