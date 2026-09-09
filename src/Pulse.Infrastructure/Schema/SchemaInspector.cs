using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Pulse.Infrastructure.Schema;

/// <summary>Compares structural metadata, not formatting of CREATE TABLE statements.</summary>
public static class SchemaInspector
{
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static async Task<List<string[]>> RowsAsync(PulseDbContext db, string sql, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(values);
        }
        return rows;
    }

    public static async Task<bool> HasHistoryAsync(PulseDbContext db, CancellationToken ct) =>
        (await RowsAsync(db, "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'", ct)).Count != 0;

    public static async Task<string> DescribeAsync(PulseDbContext db, CancellationToken ct = default)
    {
        var objects = await RowsAsync(db,
            "SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock') ORDER BY type,name", ct);
        var facts = new List<string>();
        foreach (var item in objects)
        {
            if (item[0] != "table")
            {
                // Index properties are described structurally below. Views/triggers are not ignored.
                if (item[0] != "index") facts.Add(JsonSerializer.Serialize(item));
                continue;
            }
            var name = item[1];
            facts.Add($"table:{name}");
            // PRAGMAs omit CHECK expressions, column collations, and table options.
            // Compare tokenized DDL as well so an altered constraint cannot masquerade
            // as the baseline. Whitespace and identifier quoting are not evidence.
            var tokens = Regex.Matches(item[2], "\"(?:[^\"]|\"\")*\"|'(?:[^']|'')*'|[A-Za-z_][A-Za-z_0-9]*|[0-9]+(?:\\.[0-9]+)?|[^\\s]")
                .Select(match => match.Value.StartsWith('\'') ? match.Value
                    : match.Value.StartsWith('"') ? match.Value[1..^1].Replace("\"\"", "\"").ToUpperInvariant()
                    : match.Value.ToUpperInvariant());
            facts.Add($"table-definition:{name}:" + JsonSerializer.Serialize(tokens));
            foreach (var row in await RowsAsync(db, $"PRAGMA table_xinfo({Quote(name)})", ct))
                facts.Add($"column:{name}:" + JsonSerializer.Serialize(row));
            foreach (var row in await RowsAsync(db, $"PRAGMA foreign_key_list({Quote(name)})", ct))
                facts.Add($"foreign-key:{name}:" + JsonSerializer.Serialize(row));
            foreach (var index in await RowsAsync(db, $"PRAGMA index_list({Quote(name)})", ct))
            {
                var indexName = index[1];
                // seq is an enumeration order, not schema identity.
                facts.Add($"index:{name}:" + JsonSerializer.Serialize(index.Skip(1)));
                foreach (var row in await RowsAsync(db, $"PRAGMA index_xinfo({Quote(indexName)})", ct))
                    facts.Add($"index-column:{indexName}:" + JsonSerializer.Serialize(row));
                if (index[4] == "1")
                    facts.Add("partial-index:" + objects.Single(o => o[0] == "index" && o[1] == indexName)[2]);
            }
        }
        facts.Sort(StringComparer.Ordinal);
        return string.Join('\n', facts);
    }
}
