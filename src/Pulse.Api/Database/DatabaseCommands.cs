using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pulse.Infrastructure;
using Pulse.Infrastructure.Schema;

namespace Pulse.Api.Database;

public static class DatabaseCommands
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "db", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (args.Length != 4 || args[2] != "--database" || args[1] is not ("status" or "adopt-legacy" or "upgrade"))
                throw new ArgumentException("Usage: db status|adopt-legacy|upgrade --database <sqlite-file>. Stop all application writers before adoption/upgrade.");
            var path = Path.GetFullPath(args[3]);
            if (args[1] != "upgrade" && !File.Exists(path))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { path, state = "missing" }, JsonSerializerOptions.Web));
                Environment.ExitCode = 1;
                return true;
            }
            var connection = new SqliteConnectionStringBuilder { DataSource = path,
                Mode = args[1] == "status" ? SqliteOpenMode.ReadOnly : args[1] == "upgrade" ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite };
            await using var db = new PulseDbContext(new DbContextOptionsBuilder<PulseDbContext>().UseSqlite(connection.ToString()).Options);
            var state = args[1] switch
            {
                "status" => await DatabaseLifecycle.InspectAsync(db),
                "adopt-legacy" => await DatabaseLifecycle.AdoptLegacyAsync(db),
                _ => await DatabaseLifecycle.UpgradeAsync(db),
            };
            Console.WriteLine(JsonSerializer.Serialize(new { path, state = state.State, applied = state.Applied,
                pending = state.Pending, detail = state.Detail }, JsonSerializerOptions.Web));
            Environment.ExitCode = state.State is "unknown" or "unsupported" ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Database command failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
        return true;
    }
}
