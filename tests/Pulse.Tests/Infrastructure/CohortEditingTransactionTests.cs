using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class CohortEditingTransactionTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public PulseDbContext Db { get; }
        public Guid Project { get; } = Guid.NewGuid();
        public CountingClock Clock { get; } = new();
        public CohortEditingService Service { get; }
        public Fixture()
        {
            Connection.Open();
            Db = new(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(Connection).Options);
            Service = new(Db, new CohortService(Db, Clock), Clock);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
    private sealed class CountingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }

    [Fact]
    public async Task FailedReplacement_RollsBackEarlierBulkDeletion()
    {
        await using var fixture = new Fixture();
        var db = fixture.Db;
        await db.Database.MigrateAsync();
        var a = new Person { ProjectId = fixture.Project };
        var b = new Person { ProjectId = fixture.Project };
        var cohort = new Cohort { ProjectId = fixture.Project, Name = "Existing", Type = CohortType.Static };
        db.Persons.AddRange(a, b);
        db.Cohorts.Add(cohort);
        db.CohortPersons.Add(new CohortPerson { CohortId = cohort.Id, PersonId = a.Id });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_member BEFORE INSERT ON CohortPersons
            BEGIN SELECT RAISE(ABORT, 'injected membership save failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ReplaceMembersAsync(fixture.Project, cohort.Id, [b.Id], default));
        db.ChangeTracker.Clear();
        Assert.Equal(a.Id, (await db.CohortPersons.SingleAsync()).PersonId);
    }

    [Fact]
    public async Task FailedSnapshot_RollsBackNewCohortAndMemberships()
    {
        await using var fixture = new Fixture();
        var db = fixture.Db;
        await db.Database.MigrateAsync();
        var source = new Cohort { ProjectId = fixture.Project, Name = "Source", Type = CohortType.Dynamic,
            RulesJson = "[{\"kind\":\"property\",\"property\":\"plan\",\"operator\":\"is_set\"}]" };
        db.Cohorts.Add(source);
        db.Persons.Add(new Person { ProjectId = fixture.Project, PropertiesJson = "{\"plan\":\"pro\"}" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_snapshot BEFORE INSERT ON CohortPersons
            BEGIN SELECT RAISE(ABORT, 'injected snapshot save failure'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.SnapshotAsync(fixture.Project, source.Id, "Copy", default));
        db.ChangeTracker.Clear();
        Assert.Equal(source.Id, (await db.Cohorts.SingleAsync()).Id);
        Assert.Equal(0, await db.CohortPersons.CountAsync());
    }

    [Theory]
    [InlineData(0, 201)]
    [InlineData(1001, 409)]
    public async Task Snapshot_EnforcesFullMembershipCap_AndAllowsEmpty(int count, int status)
    {
        await using var fixture = new Fixture();
        var db = fixture.Db;
        await db.Database.MigrateAsync();
        var source = new Cohort { ProjectId = fixture.Project, Name = "Source", Type = CohortType.Dynamic,
            RulesJson = "[{\"kind\":\"property\",\"property\":\"plan\",\"operator\":\"is_set\"}]" };
        db.Cohorts.Add(source);
        db.Persons.AddRange(Enumerable.Range(0, count).Select(_ => new Person { ProjectId = fixture.Project, PropertiesJson = "{\"plan\":\"pro\"}" }));
        await db.SaveChangesAsync();
        var result = await fixture.Service.SnapshotAsync(fixture.Project, source.Id, "Copy", default);
        Assert.Equal(status, result.Status);
        Assert.Equal(status == 201 ? 2 : 1, await db.Cohorts.CountAsync());
        Assert.Equal(0, await db.CohortPersons.CountAsync());
        Assert.Equal(1, fixture.Clock.Reads);
    }

    [Fact]
    public async Task Snapshot_RejectsDanglingEvaluatedPerson_WithoutSilentlyDroppingIt()
    {
        await using var fixture = new Fixture();
        var db = fixture.Db;
        await db.Database.MigrateAsync();
        var source = new Cohort { ProjectId = fixture.Project, Name = "Source", Type = CohortType.Dynamic,
            RulesJson = "[{\"kind\":\"performed_event\",\"event\":\"purchase\",\"days\":30}]" };
        db.Cohorts.Add(source);
        db.Events.Add(new AnalyticsEvent { ProjectId = fixture.Project, PersonId = Guid.NewGuid(), Name = "purchase", DistinctId = "dangling", Timestamp = fixture.Clock.Now });
        await db.SaveChangesAsync();
        Assert.Equal(409, (await fixture.Service.SnapshotAsync(fixture.Project, source.Id, "Copy", default)).Status);
        Assert.Equal(1, await db.Cohorts.CountAsync());
        Assert.Equal(0, await db.CohortPersons.CountAsync());
    }

    [Fact]
    public async Task SharedEvaluator_UsesOneClockReading_AndRetainsInclusiveLowerAndFutureSemantics()
    {
        await using var fixture = new Fixture();
        var db = fixture.Db;
        await db.Database.MigrateAsync();
        var first = new Person { ProjectId = fixture.Project };
        var second = new Person { ProjectId = fixture.Project };
        db.Persons.AddRange(first, second);
        db.Events.AddRange(new AnalyticsEvent { ProjectId = fixture.Project, PersonId = first.Id, Name = "a", DistinctId = "first", Timestamp = fixture.Clock.Now.AddDays(-1) },
            new AnalyticsEvent { ProjectId = fixture.Project, PersonId = first.Id, Name = "b", DistinctId = "first", Timestamp = fixture.Clock.Now.AddDays(2) },
            new AnalyticsEvent { ProjectId = fixture.Project, PersonId = second.Id, Name = "a", DistinctId = "second", Timestamp = fixture.Clock.Now.AddDays(-1).AddTicks(-1) });
        await db.SaveChangesAsync();
        var rules = new[] { new CohortRule(CohortRuleKind.PerformedEvent, null, "a", 1, 1), new CohortRule(CohortRuleKind.PerformedEvent, null, "b", 1, 1) };
        var result = await fixture.Service.PreviewAsync(fixture.Project, rules, 10, default);
        Assert.Equal(new[] { first.Id }, result.SamplePersonIds);
        Assert.Equal(fixture.Clock.Now, result.EvaluatedAt);
        Assert.Equal(1, fixture.Clock.Reads);
    }
}
