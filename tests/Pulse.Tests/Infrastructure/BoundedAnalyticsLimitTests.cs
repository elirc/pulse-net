using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class BoundedAnalyticsLimitTests
{
    [Fact]
    public async Task NinetyDayHourlyRangeProducesExactTwoThousandOneHundredSixtyOneBuckets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await new BoundedQueryService(fixture.Db).TrendAsync(
            Guid.NewGuid(), "none", At(0), At(0).AddDays(90), TrendInterval.Hour, [], default);
        Assert.Equal(2_161, result.Buckets.Count);
    }

    [Fact]
    public async Task TrendSqlScanAllowsExactlyTenThousandAndRejectsLimitPlusOne()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = Guid.NewGuid();
        var start = At(0);
        fixture.Db.Events.AddRange(Enumerable.Range(0, 10_001).Select(i => Event(project, "scan", i < 10_000 ? start : start.AddHours(1))));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new BoundedQueryService(fixture.Db);

        var exact = await service.TrendAsync(project, "scan", start, start.AddMinutes(1), TrendInterval.Hour, [], default);
        Assert.Equal(10_000, Assert.Single(exact.Buckets).Count);
        var error = await Assert.ThrowsAsync<QueryBudgetExceededException>(() =>
            service.TrendAsync(project, "scan", start, start.AddHours(1), TrendInterval.Hour, [], default));
        Assert.Equal(QueryBudgetReason.RowLimit, error.Reason);
    }

    [Fact]
    public async Task PropertyBytesAreChargedBeforeNonmatchingFilterAtExactBoundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var exactProject = Guid.NewGuid();
        var overProject = Guid.NewGuid();
        fixture.Db.Events.Add(Event(exactProject, "bytes", At(0), JsonDocumentOfSize(8 * 1024 * 1024)));
        fixture.Db.Events.Add(Event(overProject, "bytes", At(0), JsonDocumentOfSize(8 * 1024 * 1024 + 1)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var filter = new[] { new PropertyFilter(FilterTarget.Event, "never", FilterOperator.Equals, "matches") };
        var service = new BoundedQueryService(fixture.Db);

        var exact = await service.TrendAsync(exactProject, "bytes", At(0), At(1), TrendInterval.Hour, filter, default);
        Assert.All(exact.Buckets, b => Assert.Equal(0, b.Count));
        var error = await Assert.ThrowsAsync<QueryBudgetExceededException>(() =>
            service.TrendAsync(overProject, "bytes", At(0), At(1), TrendInterval.Hour, filter, default));
        Assert.Equal(QueryBudgetReason.ByteLimit, error.Reason);
    }

    [Fact]
    public async Task AnnotationQueryAllowsOneThousandAndRejectsOneThousandOne()
    {
        await using var fixture = await Fixture.CreateAsync();
        var project = Guid.NewGuid();
        fixture.Db.Annotations.AddRange(Enumerable.Range(0, 1_001).Select(i => new Annotation
        {
            ProjectId = project, Date = i < 1_000 ? new DateOnly(2026, 5, 1) : new DateOnly(2026, 5, 2), Content = $"a{i}",
        }));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new BoundedQueryService(fixture.Db);

        Assert.Equal(1_000, (await service.TrendAsync(project, "none", At(0), At(23), TrendInterval.Day, [], default)).Annotations.Count);
        var error = await Assert.ThrowsAsync<QueryBudgetExceededException>(() =>
            service.TrendAsync(project, "none", At(0), At(47), TrendInterval.Day, [], default));
        Assert.Equal(QueryBudgetReason.AnnotationLimit, error.Reason);
    }

    [Fact]
    public async Task SessionServiceEnforcesFiveHundredOutputsAndTenThousandInputs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var outputProject = Guid.NewGuid();
        var inputProject = Guid.NewGuid();
        var outputPerson = new Person { ProjectId = outputProject };
        var inputPerson = new Person { ProjectId = inputProject };
        fixture.Db.Persons.AddRange(outputPerson, inputPerson);
        fixture.Db.Events.AddRange(Enumerable.Range(0, 501).Select(i => Event(outputProject, "s", At(0).AddMinutes(i), personId: outputPerson.Id)));
        fixture.Db.Events.AddRange(Enumerable.Range(0, 10_001).Select(i => Event(inputProject, "s", At(0).AddMilliseconds(i), personId: inputPerson.Id)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var service = new PersonSessionService(fixture.Db);

        var exact = await service.QueryAsync(outputProject, outputPerson.Id, At(0), At(0).AddMinutes(500), 1, default);
        Assert.Equal(500, exact!.Sessions.Count); // event exactly at exclusive end is omitted
        var outputError = await Assert.ThrowsAsync<QueryBudgetExceededException>(() =>
            service.QueryAsync(outputProject, outputPerson.Id, At(0), At(0).AddMinutes(501), 1, default));
        Assert.Equal(QueryBudgetReason.OutputLimit, outputError.Reason);
        var exactInput = await service.QueryAsync(inputProject, inputPerson.Id, At(0), At(0).AddMilliseconds(10_000), 1, default);
        var exactInputSession = Assert.Single(exactInput!.Sessions);
        Assert.Equal(10_000, exactInputSession.EventCount); // event at the exclusive end is omitted
        var inputError = await Assert.ThrowsAsync<QueryBudgetExceededException>(() =>
            service.QueryAsync(inputProject, inputPerson.Id, At(0), At(1), 1, default));
        Assert.Equal(QueryBudgetReason.RowLimit, inputError.Reason);
    }

    private static AnalyticsEvent Event(Guid project, string name, DateTimeOffset timestamp, string properties = "{}", Guid? personId = null) =>
        new() { ProjectId = project, Name = name, DistinctId = Guid.NewGuid().ToString("N"), PersonId = personId, Timestamp = timestamp, PropertiesJson = properties };
    private static string JsonDocumentOfSize(int bytes) => "{\"x\":\"" + new string('a', bytes - 8) + "\"}";
    private static DateTimeOffset At(int hours) => DateTimeOffset.Parse("2026-05-01T00:00:00Z").AddHours(hours);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public PulseDbContext Db { get; }
        private Fixture(SqliteConnection connection, PulseDbContext db) { _connection = connection; Db = db; }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }
}
