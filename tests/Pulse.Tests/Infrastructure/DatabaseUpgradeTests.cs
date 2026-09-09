using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pulse.Domain.Entities;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Schema;

namespace Pulse.Tests.Infrastructure;

public class DatabaseUpgradeTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pulse-schema-{Guid.NewGuid():N}.db");
        public PulseDbContext Db { get; }
        public Fixture() => Db = new(new DbContextOptionsBuilder<PulseDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()).Options);
        public async Task LegacyAsync() => await Db.Database.ExecuteSqlRawAsync(
            await File.ReadAllTextAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures/legacy-schema.sql")));
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm", Path + "-journal" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task FreshDatabase_UpgradesAndVerifies_WithoutEnsureCreated()
    {
        await using var fixture = new Fixture();
        Assert.Equal("empty", (await DatabaseLifecycle.InspectAsync(fixture.Db)).State);
        var result = await DatabaseLifecycle.UpgradeAsync(fixture.Db);
        Assert.Equal("current", result.State);
        Assert.NotEmpty(result.Applied);
        Assert.Empty(result.Pending);
        await new VerifyDatabaseInitializer().InitializeAsync(fixture.Db);
        Assert.Equal("current", (await DatabaseLifecycle.UpgradeAsync(fixture.Db)).State);
    }

    [Fact]
    public async Task PreviousMigration_IsPendingAndRefusesStartup_ThenBackfillsExistingMembersAsAdmins()
    {
        await using var fixture = new Fixture();
        await fixture.Db.GetService<IMigrator>().MigrateAsync(fixture.Db.Database.GetMigrations().First());
        var project = Guid.NewGuid();
        var user = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.UtcTicks;
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProjectMemberships (Id, ProjectId, UserId, CreatedAt) VALUES ({membership}, {project}, {user}, {created})");
        Assert.Equal("pending", (await DatabaseLifecycle.InspectAsync(fixture.Db)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VerifyDatabaseInitializer().InitializeAsync(fixture.Db));
        Assert.Equal("current", (await DatabaseLifecycle.UpgradeAsync(fixture.Db)).State);
        var stored = await fixture.Db.ProjectMemberships.SingleAsync();
        Assert.Equal(membership, stored.Id);
        Assert.Equal(project, stored.ProjectId);
        Assert.Equal(user, stored.UserId);
        Assert.Equal(created, stored.CreatedAt.UtcTicks);
        Assert.Equal(ProjectRole.Admin, stored.Role);
        await new VerifyDatabaseInitializer().InitializeAsync(fixture.Db);
    }

    [Fact]
    public async Task LegacyAdoption_PreservesData_AndIsRepeatable()
    {
        await using var fixture = new Fixture();
        await fixture.LegacyAsync();
        var project = new Project { Name = "Preserve me", ApiKey = "pk_live_fixture", ReadKey = "rk_live_fixture",
            CreatedAt = new DateTimeOffset(2026, 3, 1, 10, 30, 0, TimeSpan.FromHours(2)) };
        var person = new Person { ProjectId = project.Id, PropertiesJson = "{\"plan\":\"starter\"}", CreatedAt = project.CreatedAt };
        var flag = new FeatureFlag { ProjectId = project.Id, Key = "preserved-flag", RolloutPercentage = 37.5, FiltersJson = "[]", CreatedAt = project.CreatedAt };
        var export = new ExportJob { ProjectId = project.Id, Type = "events", Format = "json", Status = ExportJobStatus.Completed,
            ResultContent = "[{\"name\":\"historical\"}]", RowCount = 1, ContentType = "application/json", CreatedAt = project.CreatedAt, CompletedAt = project.CreatedAt.AddMinutes(1) };
        // Seed only frozen legacy columns. Current entity mappings will evolve;
        // using SaveChanges here would try to write columns the old DB never had.
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Projects (Id, Name, ApiKey, ReadKey, CreatedAt) VALUES ({project.Id}, {project.Name}, {project.ApiKey}, {project.ReadKey}, {project.CreatedAt.UtcTicks})");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Persons (Id, ProjectId, PropertiesJson, CreatedAt) VALUES ({person.Id}, {person.ProjectId}, {person.PropertiesJson}, {person.CreatedAt.UtcTicks})");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO FeatureFlags (Id, ProjectId, Key, Name, Type, Active, RolloutPercentage, FiltersJson, VariantsJson, CreatedAt) VALUES ({flag.Id}, {flag.ProjectId}, {flag.Key}, {flag.Name}, {flag.Type.ToString()}, {flag.Active}, {flag.RolloutPercentage}, {flag.FiltersJson}, {flag.VariantsJson}, {flag.CreatedAt.UtcTicks})");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExportJobs (Id, ProjectId, Type, Format, ParamsJson, Status, ResultContent, ContentType, RowCount, CreatedAt, CompletedAt) VALUES ({export.Id}, {export.ProjectId}, {export.Type}, {export.Format}, {export.ParamsJson}, {export.Status.ToString()}, {export.ResultContent}, {export.ContentType}, {export.RowCount}, {export.CreatedAt.UtcTicks}, {export.CompletedAt!.Value.UtcTicks})");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Events (Id, ProjectId, Name, DistinctId, Timestamp, PropertiesJson) VALUES ({Guid.NewGuid()}, {project.Id}, {"historical"}, {"device-A"}, {project.CreatedAt.UtcTicks}, {"{\"amount\":12.5}"})");
        await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO QueuedEvents (ProjectId, PayloadJson, Attempts, EnqueuedAt) VALUES ({project.Id}, {"{\"Name\":\"waiting\"}"}, {2}, {project.CreatedAt.UtcTicks})");
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("legacy", (await DatabaseLifecycle.InspectAsync(fixture.Db)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLifecycle.UpgradeAsync(fixture.Db));
        var adopted = await DatabaseLifecycle.AdoptLegacyAsync(fixture.Db);
        Assert.Contains(adopted.State, new[] { "current", "pending" });
        Assert.Equal(adopted.State, (await DatabaseLifecycle.AdoptLegacyAsync(fixture.Db)).State);
        Assert.Equal("current", (await DatabaseLifecycle.UpgradeAsync(fixture.Db)).State);
        var stored = await fixture.Db.Projects.SingleAsync(p => p.Id == project.Id);
        Assert.Equal(project.Name, stored.Name);
        Assert.Equal(project.ApiKey, stored.ApiKey);
        Assert.Equal(project.ReadKey, stored.ReadKey);
        Assert.Equal(project.CreatedAt.UtcTicks, stored.CreatedAt.UtcTicks);
        Assert.Equal("{\"amount\":12.5}", (await fixture.Db.Events.SingleAsync()).PropertiesJson);
        Assert.Equal(2, (await fixture.Db.QueuedEvents.SingleAsync()).Attempts);
        var oldQueue = await fixture.Db.QueuedEvents.SingleAsync();
        Assert.Null(oldQueue.NextAttemptAt);
        Assert.Null(oldQueue.AdmissionId);
        Assert.Empty(await fixture.Db.CaptureReceipts.ToListAsync());
        var settings = await fixture.Db.ProjectIngestionStates.SingleAsync(s => s.ProjectId == project.Id);
        Assert.Null(settings.MaxPending);
        Assert.False(settings.Paused);
        var lease = await fixture.Db.ProjectIngestionLeases.SingleAsync(l => l.ProjectId == project.Id);
        Assert.Null(lease.Owner);
        Assert.Equal(0, lease.Generation);
        var savedPerson = await fixture.Db.Persons.SingleAsync();
        Assert.Equal(person.Id, savedPerson.Id);
        Assert.Equal(person.PropertiesJson, savedPerson.PropertiesJson);
        Assert.Equal(person.CreatedAt.UtcTicks, savedPerson.CreatedAt.UtcTicks);
        var savedFlag = await fixture.Db.FeatureFlags.SingleAsync();
        Assert.Equal(flag.Id, savedFlag.Id);
        Assert.Equal(flag.Key, savedFlag.Key);
        Assert.Equal(flag.RolloutPercentage, savedFlag.RolloutPercentage);
        Assert.Equal(1, savedFlag.Revision);
        var baseline = await fixture.Db.FlagVersions.SingleAsync();
        Assert.Equal(flag.Id, baseline.FlagId);
        Assert.Equal(1, baseline.Revision);
        Assert.Equal("baseline", baseline.Origin);
        Assert.Null(baseline.ActorUserId);
        Assert.Null(baseline.PersonalKeyId);
        Assert.Empty(await fixture.Db.AuditEntries.ToListAsync());
        var savedExport = await fixture.Db.ExportJobs.SingleAsync();
        Assert.Equal(export.Id, savedExport.Id);
        Assert.Equal(export.ResultContent, savedExport.ResultContent);
        Assert.Equal(export.CompletedAt, savedExport.CompletedAt);
        Assert.Equal("live", savedExport.Consistency);
        Assert.Equal(0, savedExport.AttemptGeneration);
        Assert.Null(savedExport.Owner);
        Assert.Null(savedExport.LeaseExpiresAt);
        Assert.False(savedExport.SnapshotReady);
        var retention = await fixture.Db.ProjectRetentionPolicies.SingleAsync();
        Assert.False(retention.Enabled);
        Assert.Equal(365, retention.Days);
        Assert.Equal(1, retention.Revision);
        Assert.Empty(await fixture.Db.RetentionRuns.ToListAsync());
    }

    [Fact]
    public async Task UnknownLegacySchema_IsRejected_WithoutHistoryOrDataChanges()
    {
        await using var fixture = new Fixture();
        await fixture.LegacyAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("CREATE TABLE Unexpected (Id TEXT PRIMARY KEY)");
        var before = await SchemaInspector.DescribeAsync(fixture.Db);
        Assert.Equal("unknown", (await DatabaseLifecycle.InspectAsync(fixture.Db)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLifecycle.AdoptLegacyAsync(fixture.Db));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLifecycle.UpgradeAsync(fixture.Db));
        Assert.Equal(before, await SchemaInspector.DescribeAsync(fixture.Db));
        Assert.False(await SchemaInspector.HasHistoryAsync(fixture.Db, default));
    }

    [Fact]
    public async Task ReadOnlyStatus_DoesNotModifyPopulatedDatabaseBytes()
    {
        await using var fixture = new Fixture();
        await fixture.LegacyAsync();
        await fixture.Db.Database.CloseConnectionAsync();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.Path));
        await using (var readOnly = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options))
            Assert.Equal("legacy", (await DatabaseLifecycle.InspectAsync(readOnly)).State);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(fixture.Path)));
    }

    [Fact]
    public async Task ForgedHistoryOrChangedStructure_DoesNotPassStartupVerification()
    {
        await using var fixture = new Fixture();
        await DatabaseLifecycle.UpgradeAsync(fixture.Db);
        await fixture.Db.Database.ExecuteSqlRawAsync("CREATE TABLE Unexpected (Id TEXT)");
        Assert.Equal("unknown", (await DatabaseLifecycle.InspectAsync(fixture.Db)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VerifyDatabaseInitializer().InitializeAsync(fixture.Db));
    }

    [Fact]
    public async Task MissingDatabase_StartupDoesNotCreateIt()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VerifyDatabaseInitializer().InitializeAsync(fixture.Db));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task RestoreToSeparatePath_ReproducesSupportedSchema()
    {
        await using var original = new Fixture();
        await original.LegacyAsync();
        await original.Db.Database.CloseConnectionAsync();
        await using var restored = new Fixture();
        File.Copy(original.Path, restored.Path);
        Assert.Equal("legacy", (await DatabaseLifecycle.InspectAsync(restored.Db)).State);
        await DatabaseLifecycle.AdoptLegacyAsync(restored.Db);
        Assert.Equal("current", (await DatabaseLifecycle.UpgradeAsync(restored.Db)).State);
        Assert.Equal("legacy", (await DatabaseLifecycle.InspectAsync(original.Db)).State);
    }
}
