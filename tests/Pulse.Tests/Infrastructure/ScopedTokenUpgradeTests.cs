using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pulse.Domain.Entities;

namespace Pulse.Tests.Infrastructure;

public class ScopedTokenUpgradeTests
{
    [Fact]
    public async Task ExistingKeysBecomeExplicitLegacyMode_WithoutChangingHashSuffixOrLifetime()
    {
        await using var f = new IngestionReliabilityFixture(); await using var db = f.Open();
        await db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("AddEventRetention", StringComparison.Ordinal)));
        var id = Guid.NewGuid(); var user = Guid.NewGuid(); var created = f.Clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PersonalApiKeys (Id, UserId, Name, KeyHash, KeySuffix, CreatedAt) VALUES ({id}, {user}, {"preserved"}, {new string('a', 64)}, {"abcd"}, {created.UtcTicks})");
        await db.Database.MigrateAsync();
        var key = await db.PersonalApiKeys.SingleAsync(); Assert.Equal(id, key.Id); Assert.Equal(user, key.UserId);
        Assert.Equal("preserved", key.Name); Assert.Equal(new string('a', 64), key.KeyHash); Assert.Equal("abcd", key.KeySuffix); Assert.Equal(created, key.CreatedAt);
        Assert.Equal(PersonalKeyMode.LegacyUnrestricted, key.Mode); Assert.Null(key.ExpiresAt);
        Assert.Empty(await db.PersonalKeyProjects.ToListAsync()); Assert.Empty(await db.PersonalKeyScopes.ToListAsync());
    }
}
