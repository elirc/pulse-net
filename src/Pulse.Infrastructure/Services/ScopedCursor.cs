using System.Text.Json;

namespace Pulse.Infrastructure.Services;

/// <summary>A validated position bound to a query, never an authorization credential or snapshot.</summary>
public record ScopedCursor(int Version, string Purpose, Guid ProjectId, Guid? ResourceId, string? Filter, long Ticks, Guid Id)
{
    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? input, string purpose, Guid projectId, Guid? resourceId, string? filter, out ScopedCursor? position)
    {
        position = null;
        if (input is null) return true;
        if (input.Length is 0 or > 2048 || input.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try
        {
            var raw = input.Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=');
            var decoded = JsonSerializer.Deserialize<ScopedCursor>(Convert.FromBase64String(raw));
            if (decoded is null || decoded.Version != 1 || decoded.Purpose != purpose || decoded.ProjectId != projectId ||
                decoded.ResourceId != resourceId || decoded.Filter != filter || decoded.Ticks < DateTimeOffset.MinValue.UtcTicks ||
                decoded.Ticks > DateTimeOffset.MaxValue.UtcTicks || decoded.Id == Guid.Empty) return false;
            position = decoded;
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return false; }
    }
}
