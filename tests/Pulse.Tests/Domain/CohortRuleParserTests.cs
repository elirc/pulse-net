using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class CohortRuleParserTests
{
    [Fact]
    public void Parses_PropertyAndBehaviorRules()
    {
        const string json = """
            [
              { "kind": "property", "property": "plan", "operator": "equals", "value": "pro" },
              { "kind": "performed_event", "event": "purchase", "days": 30, "minCount": 2 }
            ]
            """;

        Assert.True(CohortRuleParser.TryParse(json, out var rules, out _));
        Assert.Equal(2, rules.Count);

        var property = rules[0];
        Assert.Equal(CohortRuleKind.Property, property.Kind);
        Assert.Equal(FilterTarget.Person, property.Filter!.Target);
        Assert.Equal("plan", property.Filter.Property);
        Assert.Equal(FilterOperator.Equals, property.Filter.Operator);
        Assert.Equal("pro", property.Filter.Value);

        var behavior = rules[1];
        Assert.Equal(CohortRuleKind.PerformedEvent, behavior.Kind);
        Assert.Equal("purchase", behavior.Event);
        Assert.Equal(30, behavior.Days);
        Assert.Equal(2, behavior.MinCount);
    }

    [Fact]
    public void PerformedEvent_DefaultsTo30Days1Count()
    {
        const string json = """[{ "kind": "performed_event", "event": "login" }]""";

        Assert.True(CohortRuleParser.TryParse(json, out var rules, out _));
        Assert.Equal(30, rules[0].Days);
        Assert.Equal(1, rules[0].MinCount);
    }

    [Theory]
    [InlineData("""[{ "kind": "teleport" }]""")]
    [InlineData("""[{ "kind": "property", "operator": "equals", "value": "x" }]""")] // no property
    [InlineData("""[{ "kind": "property", "property": "plan", "operator": "regex", "value": "x" }]""")]
    [InlineData("""[{ "kind": "property", "property": "plan", "operator": "equals" }]""")] // no value
    [InlineData("""[{ "kind": "performed_event" }]""")] // no event
    [InlineData("""[{ "kind": "performed_event", "event": "x", "days": 0 }]""")]
    [InlineData("""[{ "kind": "performed_event", "event": "x", "minCount": 0 }]""")]
    [InlineData("""{ "kind": "property" }""")] // not an array
    [InlineData("not json")]
    public void InvalidRules_FailWithError(string json)
    {
        Assert.False(CohortRuleParser.TryParse(json, out _, out var error));
        Assert.NotEqual(string.Empty, error);
    }
}
