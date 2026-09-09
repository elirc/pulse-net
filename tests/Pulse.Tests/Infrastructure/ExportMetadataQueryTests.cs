using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class ExportMetadataQueryTests
{
    private sealed class Queries : DbCommandInterceptor
    {
        public List<string> Reads { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Reads.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task History_DoesNotSelectInlineContentOrParameters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queries = new Queries();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).AddInterceptors(queries).Options);
        await db.Database.MigrateAsync();
        var project = Guid.NewGuid();
        db.ExportJobs.Add(new ExportJob { ProjectId = project, Type = "events", Format = "json", Status = ExportJobStatus.Completed,
            ResultContent = new string('x', 100000), ParamsJson = "{\"marker\":\"private\"}" });
        await db.SaveChangesAsync();
        queries.Reads.Clear();
        var service = new ExportJobOperationsService(db, new ExportSignal(), TimeProvider.System);
        Assert.Single((await service.HistoryAsync(project, null, null, 25, ":", null, default)).Jobs);
        var sql = Assert.Single(queries.Reads);
        Assert.DoesNotContain("ResultContent", sql);
        Assert.DoesNotContain("ParamsJson", sql);
    }
}
