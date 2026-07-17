using System.Text.Json;

namespace Pulse.Domain;

/// <summary>
/// Pure JSON-merge rules for person properties, mirroring PostHog semantics:
/// <c>$set</c> overwrites existing keys, <c>$set_once</c> only fills keys that
/// are not present yet.
/// </summary>
public static class PersonPropertyMerger
{
    /// <summary>Applies <paramref name="set"/> then <paramref name="setOnce"/> to <paramref name="existingJson"/>.</summary>
    public static string Apply(string existingJson, JsonElement? set, JsonElement? setOnce)
    {
        var merged = Parse(existingJson);

        if (set is { ValueKind: JsonValueKind.Object } setObject)
        {
            foreach (var property in setObject.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        if (setOnce is { ValueKind: JsonValueKind.Object } setOnceObject)
        {
            foreach (var property in setOnceObject.EnumerateObject())
            {
                if (!merged.ContainsKey(property.Name))
                {
                    merged[property.Name] = property.Value.Clone();
                }
            }
        }

        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// Merges the properties of two persons being combined by an identity
    /// merge. The winner's values take precedence; the loser only contributes
    /// keys the winner does not have.
    /// </summary>
    public static string MergePersons(string winnerJson, string loserJson)
    {
        var winner = Parse(winnerJson);
        var loser = Parse(loserJson);

        foreach (var (key, value) in loser)
        {
            winner.TryAdd(key, value);
        }

        return JsonSerializer.Serialize(winner);
    }

    private static Dictionary<string, JsonElement> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
