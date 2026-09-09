using System.Security.Cryptography;
using System.Text.Json;

namespace Pulse.Infrastructure.Services;

/// <summary>Version 1: ordinal object-key ordering, array ordering retained, original JSON number spelling retained.</summary>
public static class CaptureFingerprint
{
    public const int Version = 1;
    public static bool HasUniqueObjectMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || !HasUniqueObjectMembers(property.Value)) return false;
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!HasUniqueObjectMembers(item)) return false;
        return true;
    }
    public static bool TryCreate(string name, string identity, DateTimeOffset? timestamp, string propertiesJson, out string fingerprint)
    {
        fingerprint = "";
        try
        {
            using var document = JsonDocument.Parse(propertiesJson);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray(); writer.WriteNumberValue(Version);
                writer.WriteStringValue(name.Trim()); writer.WriteStringValue(identity.Trim());
                if (timestamp is { } supplied) writer.WriteNumberValue(supplied.UtcTicks);
                else writer.WriteNullValue(); // Omitted timestamp does not change across retries.
                if (!WriteCanonical(writer, document.RootElement)) return false;
                writer.WriteEndArray(); writer.Flush();
            }
            fingerprint = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToList();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Count) return false;
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (!WriteCanonical(writer, property.Value)) return false;
                }
                writer.WriteEndObject(); return true;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) if (!WriteCanonical(writer, item)) return false;
                writer.WriteEndArray(); return true;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText()); return true;
            default:
                value.WriteTo(writer); return true;
        }
    }
}
