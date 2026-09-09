using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class FlagGovernanceUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_PreservesRawLegacyConfiguration_AndAddsOneHonestBaseline(bool oversized)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260908195641_AddProjectRoles");
        var id = Guid.NewGuid(); var project = Guid.NewGuid();
        var filters = oversized ? "[" + new string('x', 70000) : "{malformed legacy payload";
        var name = "quote \" newline\n 日本語";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO FeatureFlags (Id, ProjectId, Key, Name, Type, Active, RolloutPercentage, FiltersJson, VariantsJson, CreatedAt) VALUES ({id}, {project}, {"preserved"}, {name}, {"Boolean"}, {true}, {37.5}, {filters}, {"[]"}, {DateTimeOffset.UtcNow.UtcTicks})");
        await db.Database.MigrateAsync();
        var flag = await db.FeatureFlags.SingleAsync(); Assert.Equal(id, flag.Id); Assert.Equal(1, flag.Revision); Assert.Equal(filters, flag.FiltersJson);
        var version = await db.FlagVersions.SingleAsync();
        var config = JsonSerializer.Deserialize<FlagConfiguration>(version.ConfigJson, JsonSerializerOptions.Web)!;
        Assert.Equal(name, config.Name); Assert.Equal(filters, config.FiltersJson); Assert.True(config.Active); Assert.Equal(37.5, config.RolloutPercentage);
        Assert.Equal("baseline", version.Origin); Assert.Null(version.ActorUserId); Assert.Empty(await db.AuditEntries.ToListAsync());
        await db.Database.MigrateAsync(); Assert.Equal(1, await db.FlagVersions.CountAsync());
        Assert.Equal(409, (await new FlagMutationService(db, TimeProvider.System).RestoreAsync(flag, 1, 1, new(Guid.NewGuid()), default)).Status);
    }
}
