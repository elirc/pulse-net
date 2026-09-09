using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class FlagReadReuseTests
{
    private sealed class Reads : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public int From(string table) => Commands.Count(sql => sql.Contains($"FROM \"{table}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Batch_LoadsSharedDataOnce_AndDoesNotCacheAcrossCalls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new Reads();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.MigrateAsync();
        var project = Guid.NewGuid();
        var person = new Person { ProjectId = project };
        var cohort = new Cohort { ProjectId = project, Name = "Shared", Type = CohortType.Static };
        db.Persons.Add(person);
        db.Cohorts.Add(cohort);
        db.CohortPersons.Add(new CohortPerson { CohortId = cohort.Id, PersonId = person.Id });
        db.PersonDistinctIds.AddRange(new PersonDistinctId { ProjectId = project, PersonId = person.Id, DistinctId = "alice" },
            new PersonDistinctId { ProjectId = project, PersonId = person.Id, DistinctId = "alias" });
        var targeting = JsonSerializer.Serialize(new[] { new { type = "cohort", value = cohort.Id.ToString() } });
        db.FeatureFlags.AddRange(new FeatureFlag { ProjectId = project, Key = "A", FiltersJson = targeting },
            new FeatureFlag { ProjectId = project, Key = "B", FiltersJson = targeting });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new FeatureFlagService(db, new CohortService(db, TimeProvider.System));
        reads.Commands.Clear();
        var first = (await service.EvaluateBatchAsync(project, ["alice", "alias", "unknown"], ["A", "B"], default))!;
        Assert.All(first.Results.Take(2), row => Assert.All(row.FeatureFlags.Values, value => Assert.Equal(true, value)));
        Assert.All(first.Results[2].FeatureFlags.Values, value => Assert.Equal(false, value));
        Assert.Equal(1, reads.From("FeatureFlags"));
        Assert.Equal(1, reads.From("PersonDistinctIds"));
        Assert.Equal(1, reads.From("Persons"));
        Assert.Equal(1, reads.From("CohortPersons"));
        await db.CohortPersons.ExecuteDeleteAsync();
        reads.Commands.Clear();
        var second = (await service.EvaluateBatchAsync(project, ["alice", "alias"], ["A", "B"], default))!;
        Assert.All(second.Results, row => Assert.All(row.FeatureFlags.Values, value => Assert.Equal(false, value)));
        Assert.Equal(1, reads.From("CohortPersons"));
        Assert.Equal(1, await db.Persons.CountAsync());
    }
}
