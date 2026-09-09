using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class DiscoveryBudgetTests
{
    [Theory]
    [InlineData(10001, "{}", 422)]
    [InlineData(1, "oversize", 422)]
    [InlineData(1, "private-invalid-json", 409)]
    [InlineData(1, "[]", 409)]
    public async Task FailedPropertyScans_ReturnNoPartialStatistics(int rows, string json, int status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var project = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        if (json == "oversize") json = "{\"value\":\"" + new string('x', 8 * 1024 * 1024) + "\"}";
        db.Events.AddRange(Enumerable.Range(0, rows).Select(_ => new AnalyticsEvent { ProjectId = project, Name = "a", DistinctId = "p", Timestamp = time, PropertiesJson = json }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new ProjectDiscoveryService(db, new IngestionOperationsService(db, new IngestionSignal(), TimeProvider.System), TimeProvider.System);
        var result = await service.PropertyValuesAsync(project, "a", "value", time, time.AddDays(1), 10, default);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Result);
        Assert.DoesNotContain("private-invalid-json", result.Detail!);
        var narrower = await service.PropertyValuesAsync(project, "a", "value", time.AddHours(1), time.AddDays(1), 10, default);
        Assert.Equal(200, narrower.Status);
        Assert.Equal(0, narrower.Result!.TotalEvents);
    }

    [Theory]
    [InlineData(0, 0, null, "ok", "")]
    [InlineData(999, 0, 59.99, "ok", "")]
    [InlineData(1000, 0, null, "attention", "queue_depth_high")]
    [InlineData(2, 0, 60.0, "attention", "oldest_pending_too_old")]
    [InlineData(1001, 1, 61.0, "attention", "dead_letters_present,queue_depth_high,oldest_pending_too_old")]
    public void StatusPolicy_UsesInclusiveThresholdsAndFixedReasonOrder(int pending, int deadLetters, double? age, string expected, string codes)
    {
        var now = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var result = IngestionStatusPolicy.Classify(new(pending, deadLetters, null, age), new(), now);
        Assert.Equal(expected, result.Status);
        Assert.Equal(codes, string.Join(',', result.Reasons));
        Assert.Equal(now, result.ObservedAt);
    }
}
