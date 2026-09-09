using System.Text.Json;

namespace Pulse.Infrastructure.Services;

public record AuditCursor(int Version, string Purpose, Guid ProjectId, string? Action, long BeforeSequence)
{
    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static bool TryDecode(string? text, Guid projectId, string? action, out AuditCursor? cursor)
    {
        cursor = null;
        if (text is null) return true;
        if (text.Length is 0 or > 2048 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            cursor = JsonSerializer.Deserialize<AuditCursor>(Convert.FromBase64String(padded));
            return cursor is { Version: 1, Purpose: "management-audit", BeforeSequence: > 0 } && cursor.ProjectId == projectId && cursor.Action == action;
        }
        catch (Exception error) when (error is JsonException or FormatException) { return false; }
    }
}
