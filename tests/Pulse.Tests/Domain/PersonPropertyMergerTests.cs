using System.Text.Json;
using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class PersonPropertyMergerTests
{
    [Fact]
    public void Apply_Set_OverwritesExistingKeys()
    {
        var result = PersonPropertyMerger.Apply(
            """{"plan":"free","name":"Ada"}""",
            Element("""{"plan":"pro"}"""),
            setOnce: null);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal("pro", parsed["plan"].GetString());
        Assert.Equal("Ada", parsed["name"].GetString());
    }

    [Fact]
    public void Apply_SetOnce_DoesNotOverwriteExistingKeys()
    {
        var result = PersonPropertyMerger.Apply(
            """{"initial_referrer":"google"}""",
            set: null,
            setOnce: Element("""{"initial_referrer":"bing","first_seen":"2026-01-01"}"""));

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal("google", parsed["initial_referrer"].GetString());
        Assert.Equal("2026-01-01", parsed["first_seen"].GetString());
    }

    [Fact]
    public void Apply_SetWinsOverSetOnce_ForSameNewKey()
    {
        var result = PersonPropertyMerger.Apply(
            "{}",
            Element("""{"email":"new@example.com"}"""),
            Element("""{"email":"old@example.com"}"""));

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal("new@example.com", parsed["email"].GetString());
    }

    [Fact]
    public void Apply_PreservesNonStringValues()
    {
        var result = PersonPropertyMerger.Apply(
            "{}",
            Element("""{"age":42,"tags":["a","b"],"active":true}"""),
            setOnce: null);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal(42, parsed["age"].GetInt32());
        Assert.Equal(JsonValueKind.Array, parsed["tags"].ValueKind);
        Assert.True(parsed["active"].GetBoolean());
    }

    [Fact]
    public void Apply_ToleratesMalformedExistingJson()
    {
        var result = PersonPropertyMerger.Apply(
            "not-json",
            Element("""{"plan":"pro"}"""),
            setOnce: null);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal("pro", parsed["plan"].GetString());
    }

    [Fact]
    public void MergePersons_WinnerValuesTakePrecedence()
    {
        var result = PersonPropertyMerger.MergePersons(
            """{"email":"real@example.com","plan":"pro"}""",
            """{"email":"anon@device","device":"ios"}""");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result)!;
        Assert.Equal("real@example.com", parsed["email"].GetString());
        Assert.Equal("pro", parsed["plan"].GetString());
        Assert.Equal("ios", parsed["device"].GetString());
    }

    private static JsonElement Element(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json);
}
