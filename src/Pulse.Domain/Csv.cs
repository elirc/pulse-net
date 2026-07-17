using System.Text;

namespace Pulse.Domain;

/// <summary>Minimal RFC 4180 CSV writing.</summary>
public static class Csv
{
    /// <summary>Quotes a field when it contains a comma, quote or newline.</summary>
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        return field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
    }

    /// <summary>Joins fields into one CSV line (no trailing newline).</summary>
    public static string Line(params string?[] fields) =>
        string.Join(',', fields.Select(Escape));

    /// <summary>Builds a document from a header and rows, using \n line endings.</summary>
    public static string Document(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
