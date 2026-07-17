using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class PropertyFilterEvaluatorTests
{
    private const string Json = """
        { "url": "/pricing", "plan": "pro", "count": 42, "active": true, "ghost": null }
        """;

    [Theory]
    [InlineData("url", "/pricing", true)]
    [InlineData("url", "/PRICING", false)] // equals is case-sensitive
    [InlineData("url", "/docs", false)]
    [InlineData("count", "42", true)] // numbers compare via raw text
    [InlineData("active", "true", true)]
    [InlineData("missing", "x", false)]
    public void Equals_ComparesCanonicalStringForm(string property, string value, bool expected)
    {
        var filter = new PropertyFilter(FilterTarget.Event, property, FilterOperator.Equals, value);

        Assert.Equal(expected, PropertyFilterEvaluator.MatchesSingle(Json, filter));
    }

    [Theory]
    [InlineData("url", "pric", true)]
    [InlineData("url", "PRIC", true)] // contains is case-insensitive
    [InlineData("url", "docs", false)]
    [InlineData("missing", "x", false)]
    public void Contains_MatchesSubstrings(string property, string value, bool expected)
    {
        var filter = new PropertyFilter(FilterTarget.Event, property, FilterOperator.Contains, value);

        Assert.Equal(expected, PropertyFilterEvaluator.MatchesSingle(Json, filter));
    }

    [Theory]
    [InlineData("url", true)]
    [InlineData("missing", false)]
    [InlineData("ghost", false)] // JSON null counts as not set
    public void IsSet_ChecksPresence(string property, bool expected)
    {
        var isSet = new PropertyFilter(FilterTarget.Event, property, FilterOperator.IsSet, null);
        var isNotSet = new PropertyFilter(FilterTarget.Event, property, FilterOperator.IsNotSet, null);

        Assert.Equal(expected, PropertyFilterEvaluator.MatchesSingle(Json, isSet));
        Assert.Equal(!expected, PropertyFilterEvaluator.MatchesSingle(Json, isNotSet));
    }

    [Fact]
    public void Matches_CombinesFiltersWithAnd()
    {
        List<PropertyFilter> both =
        [
            new(FilterTarget.Event, "url", FilterOperator.Equals, "/pricing"),
            new(FilterTarget.Event, "plan", FilterOperator.Equals, "pro"),
        ];
        List<PropertyFilter> oneWrong =
        [
            new(FilterTarget.Event, "url", FilterOperator.Equals, "/pricing"),
            new(FilterTarget.Event, "plan", FilterOperator.Equals, "free"),
        ];

        Assert.True(PropertyFilterEvaluator.Matches(Json, both));
        Assert.False(PropertyFilterEvaluator.Matches(Json, oneWrong));
        Assert.True(PropertyFilterEvaluator.Matches(Json, []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[1,2]")]
    public void DegenerateDocuments_NeverMatchValueOperators(string? json)
    {
        var filter = new PropertyFilter(FilterTarget.Event, "url", FilterOperator.Equals, "/pricing");

        Assert.False(PropertyFilterEvaluator.MatchesSingle(json, filter));
        Assert.Null(PropertyFilterEvaluator.GetValue(json, "url"));
    }
}
