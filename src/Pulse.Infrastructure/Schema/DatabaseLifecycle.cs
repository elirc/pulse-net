using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Pulse.Infrastructure.Schema;

public record DatabaseState(string State, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending, string? Detail = null);

public interface IDatabaseInitializer
{
    Task InitializeAsync(PulseDbContext db, CancellationToken ct = default);
}

/// <summary>Application startup verifies; offline maintenance commands perform upgrades.</summary>
public sealed class VerifyDatabaseInitializer : IDatabaseInitializer
{
    public async Task InitializeAsync(PulseDbContext db, CancellationToken ct = default)
    {
        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        if (!File.Exists(dataSource))
            throw new InvalidOperationException($"Database is missing. Run the offline db upgrade command with --database {Path.GetFullPath(dataSource)} before starting Pulse.");
        var state = await DatabaseLifecycle.InspectAsync(db, ct);
        if (state.State != "current")
            throw new InvalidOperationException($"Database state is '{state.State}'. Stop writers and run db status, then db adopt-legacy or db upgrade as indicated. {state.Detail}");
    }
}

public static class DatabaseLifecycle
{
    private static string[] Migrations(PulseDbContext db)
    {
        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length == 0) throw new InvalidOperationException("No migrations were found. Build the migration-bearing application first.");
        return migrations;
    }

    private static async Task<string> ReferenceAsync(string migration, CancellationToken ct)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var reference = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection).Options);
        await reference.GetService<IMigrator>().MigrateAsync(migration, ct);
        return await SchemaInspector.DescribeAsync(reference, ct);
    }

    public static async Task<DatabaseState> InspectAsync(PulseDbContext db, CancellationToken ct = default)
    {
        var known = Migrations(db);
        var schema = await SchemaInspector.DescribeAsync(db, ct);
        if (!await SchemaInspector.HasHistoryAsync(db, ct))
        {
            if (schema.Length == 0) return new("empty", [], known);
            return schema == await ReferenceAsync(known[0], ct)
                ? new("legacy", [], known, "Verified baseline schema; offline adoption is required.")
                : new("unknown", [], known, "Schema does not match the supported legacy baseline. No automatic adoption is allowed.");
        }
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (!known.Take(applied.Length).SequenceEqual(applied))
            return new("unsupported", applied, [], "Migration history is not a supported prefix of this application's migrations.");
        if (applied.Length == 0)
            return new(schema.Length == 0 ? "empty" : "unknown", applied, known, "Empty history does not establish that a populated schema is supported.");
        if (schema != await ReferenceAsync(applied[^1], ct))
            return new("unknown", applied, known.Except(applied).ToArray(), "Recorded migration history and structural schema disagree.");
        return new(applied.Length == known.Length ? "current" : "pending", applied, known.Skip(applied.Length).ToArray());
    }

    public static async Task<DatabaseState> AdoptLegacyAsync(PulseDbContext db, CancellationToken ct = default)
    {
        var known = Migrations(db);
        var reference = await ReferenceAsync(known[0], ct);
        await db.Database.OpenConnectionAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        // Acquire a write lock before checking, so the checked structure cannot change before adoption.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var enlistment = await db.Database.UseTransactionAsync(transaction, ct);
        if (await SchemaInspector.HasHistoryAsync(db, ct))
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            if (!applied.Contains(known[0])) throw new InvalidOperationException("Existing history is incomplete or unsupported; adoption refused.");
            await transaction.CommitAsync(ct);
            await db.Database.UseTransactionAsync(null, ct);
            return await InspectAsync(db, ct);
        }
        if (await SchemaInspector.DescribeAsync(db, ct) != reference)
            throw new InvalidOperationException("Legacy schema mismatch. Adoption refused without changing application data or history.");
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), ct);
        var version = typeof(DbContext).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(known[0], version)), ct);
        await transaction.CommitAsync(ct);
        await db.Database.UseTransactionAsync(null, ct);
        return await InspectAsync(db, ct);
    }

    public static async Task<DatabaseState> UpgradeAsync(PulseDbContext db, CancellationToken ct = default)
    {
        var state = await InspectAsync(db, ct);
        if (state.State is not ("empty" or "pending" or "current"))
            throw new InvalidOperationException($"Cannot upgrade database state '{state.State}'. {state.Detail}");
        await db.Database.MigrateAsync(ct);
        return await InspectAsync(db, ct);
    }
}
