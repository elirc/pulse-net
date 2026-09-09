using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pulse.Domain.Entities;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class FlagVersionHistoryTests
{
    [Fact]
    public async Task Restore_CreatesNewRevisionAndPreservesHistoricalBytesAndIdentity()
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.Open();
        var before = await fixture.FlagAsync(db);
        var candidate = await fixture.FlagAsync(db);
        candidate.Name = "changed"; candidate.Active = false; candidate.RolloutPercentage = 80;
        Assert.Equal(200, (await fixture.Mutations(db).UpdateAsync(candidate, 1, fixture.Actor, default)).Status);
        db.ChangeTracker.Clear();
        var bytes = await db.FlagVersions.AsNoTracking().ToDictionaryAsync(v => v.Revision, v => v.ConfigJson);
        var result = await fixture.Mutations(db).RestoreAsync(await fixture.FlagAsync(db), 1, 2, fixture.Actor, default);
        Assert.Equal(200, result.Status);
        Assert.Equal(3, result.Flag!.Revision);
        Assert.Equal(before.Name, result.Flag.Name);
        Assert.Equal(before.Active, result.Flag.Active);
        Assert.Equal(before.RolloutPercentage, result.Flag.RolloutPercentage);
        Assert.Equal((before.Id, before.Key, before.ProjectId, before.CreatedAt), (result.Flag.Id, result.Flag.Key, result.Flag.ProjectId, result.Flag.CreatedAt));
        foreach (var pair in bytes) Assert.Equal(pair.Value, (await db.FlagVersions.SingleAsync(v => v.FlagId == fixture.FlagId && v.Revision == pair.Key)).ConfigJson);
        Assert.Equal(1, (await db.FlagVersions.SingleAsync(v => v.FlagId == fixture.FlagId && v.Revision == 3)).RestoredFromRevision);
        Assert.Equal(412, (await fixture.Mutations(db).RestoreAsync(await fixture.FlagAsync(db), 1, 2, fixture.Actor, default)).Status);
        Assert.Equal(3, await db.FlagVersions.CountAsync());
    }

    [Fact]
    public async Task Retention_KeepsExactlyNewest100IncludingCurrent_AndRejectsPrunedTarget()
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.Open();
        var candidate = await fixture.FlagAsync(db);
        var config = FlagConfiguration.From(candidate).Serialize();
        db.FlagVersions.AddRange(Enumerable.Range(2, 99).Select(revision => new FlagVersion { ProjectId = fixture.ProjectId,
            FlagId = fixture.FlagId, Revision = revision, ConfigJson = config, RecordedAt = fixture.Clock.Now, Origin = "updated" }));
        await db.FeatureFlags.Where(f => f.Id == fixture.FlagId).ExecuteUpdateAsync(s => s.SetProperty(f => f.Revision, 100L));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(200, (await fixture.Mutations(db).UpdateAsync(await fixture.FlagAsync(db), 100, fixture.Actor, default)).Status);
        Assert.Equal(100, await db.FlagVersions.CountAsync());
        Assert.Equal(2, await db.FlagVersions.MinAsync(v => v.Revision));
        Assert.Equal(101, await db.FlagVersions.MaxAsync(v => v.Revision));
        Assert.Equal(404, (await fixture.Mutations(db).RestoreAsync(await fixture.FlagAsync(db), 1, 101, fixture.Actor, default)).Status);
        Assert.Equal(101, (await fixture.FlagAsync(db)).Revision);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("bad-variants")]
    [InlineData("foreign-cohort")]
    [InlineData("deleted-cohort")]
    public async Task UnsupportedHistoricalConfiguration_IsRejectedWithoutNewEvidence(string scenario)
    {
        await using var fixture = new GovernanceFixture();
        await fixture.InitializeAsync();
        await using var db = fixture.Open();
        var flag = await fixture.FlagAsync(db);
        var cohortId = Guid.NewGuid();
        if (scenario == "foreign-cohort")
        {
            db.Cohorts.Add(new Cohort { Id = cohortId, ProjectId = Guid.NewGuid(), Name = "private" });
            await db.SaveChangesAsync();
        }
        var config = FlagConfiguration.From(flag) with { FiltersJson = JsonSerializer.Serialize(new[] { new { type = "cohort", value = cohortId.ToString() } }) };
        if (scenario == "bad-variants") config = config with { Type = "multivariate", VariantsJson = "[]", FiltersJson = "[]" };
        var raw = scenario == "invalid-json" ? "{" : config.Serialize();
        await db.FlagVersions.Where(v => v.FlagId == flag.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.ConfigJson, raw));
        Assert.Equal(409, (await fixture.Mutations(db).RestoreAsync(flag, 1, 1, fixture.Actor, default)).Status);
        Assert.Equal(1, (await fixture.FlagAsync(db)).Revision);
        Assert.Equal(1, await db.AuditEntries.CountAsync());
        Assert.Equal(1, await db.FlagVersions.CountAsync());
    }
}
