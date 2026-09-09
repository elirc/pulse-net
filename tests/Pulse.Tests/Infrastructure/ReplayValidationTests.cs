using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ReplayValidationTests
{
    [Theory]
    [InlineData("broken", "invalid-envelope-json")]
    [InlineData("null", "missing-envelope")]
    [InlineData("{\"DistinctId\":\"p\",\"PropertiesJson\":\"{}\"}", "missing-event-name")]
    [InlineData("{\"Name\":\"a\",\"PropertiesJson\":\"{}\"}", "missing-distinct-id")]
    [InlineData("{\"Name\":\"a\",\"DistinctId\":\"p\"}", "missing-properties-json")]
    [InlineData("{\"Name\":\"a\",\"DistinctId\":\"p\",\"PropertiesJson\":\"broken-private-marker\"}", "invalid-properties-json")]
    [InlineData("{\"Name\":\"a\",\"DistinctId\":\"p\",\"PropertiesJson\":\"[]\"}", "properties-not-object")]
    public void Issues_AreSpecificAndDoNotEchoPayload(string json, string code)
    {
        var result = ReplayEnvelopeValidator.Validate(json);
        Assert.False(result.Replayable);
        Assert.Equal(code, Assert.Single(result.Issues).Code);
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task LaterBatchStorageFailure_PreservesEarlierCommitAndUnattemptedLetters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var project = Guid.NewGuid();
        var letters = new[] { "first", "second", "third" }.Select(name => new DeadLetterEvent { ProjectId = project, Error = "old",
            PayloadJson = JsonSerializer.Serialize(new IncomingEvent(name, "p", null, "{}")) }).ToArray();
        db.DeadLetterEvents.AddRange(letters);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_second BEFORE INSERT ON QueuedEvents WHEN instr(NEW.PayloadJson, 'second') > 0
            BEGIN SELECT RAISE(ABORT, 'injected second enqueue failure'); END;
            """);
        var service = new IngestionOperationsService(db, new IngestionSignal(), TimeProvider.System);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReplayBatchAsync(project, letters.Select(d => d.Id).ToArray(), default));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.QueuedEvents.CountAsync());
        Assert.Contains("first", (await db.QueuedEvents.SingleAsync()).PayloadJson);
        Assert.False(await db.DeadLetterEvents.AnyAsync(d => d.Id == letters[0].Id));
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letters[1].Id));
        Assert.True(await db.DeadLetterEvents.AnyAsync(d => d.Id == letters[2].Id));
    }
}
