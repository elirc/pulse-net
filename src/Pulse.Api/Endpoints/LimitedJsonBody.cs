using System.Text.Json;

namespace Pulse.Api.Endpoints;

internal static class LimitedJsonBody
{
    public static async Task<(JsonElement? Value, IResult? Error)> ReadAsync(HttpRequest request, int maximumBytes, CancellationToken ct)
    {
        if (request.ContentLength > maximumBytes) return (null, Results.Problem("Request body exceeds the allowed size.", statusCode: 413));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximumBytes + 1 - (int)buffer.Length)), ct);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maximumBytes) return (null, Results.Problem("Request body exceeds the allowed size.", statusCode: 413));
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException) { return (null, InputRules.Problem("body", "Body must be valid JSON.")); }
    }
}
