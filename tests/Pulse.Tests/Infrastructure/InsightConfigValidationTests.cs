using System.Text.Json;
using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class InsightConfigValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0", "{}", "type")]
    [InlineData("trend", "null", "config")]
    [InlineData("trend", "[]", "config")]
    [InlineData("trend", "{}", "config.event")]
    [InlineData("trend", "{\"event\":\"a\",\"interval\":\"0\"}", "config.interval")]
    [InlineData("trend", "{\"event\":\"a\",\"interval\":null}", "config.interval")]
    [InlineData("trend", "{\"event\":\"a\",\"filters\":{}}", "config.filters")]
    [InlineData("trend", "{\"event\":\"a\",\"from\":\"bad\"}", "config.from")]
    [InlineData("trend", "{\"event\":\"a\",\"from\":\"2026-09-09\",\"to\":\"2026-09-08\"}", "config.from")]
    [InlineData("trend", "{\"event\":\"a\",\"from\":\"2026-01-01\",\"to\":\"2026-04-02\"}", "config.from")]
    [InlineData("trend", "{\"event\":\"a\",\"breakdownLimit\":2.5}", "config.breakdownLimit")]
    [InlineData("funnel", "{\"steps\":[\"a\",3]}", "config.steps[1]")]
    [InlineData("funnel", "{\"steps\":[\"a\",\" \"],\"windowDays\":\"14\"}", "config.windowDays")]
    [InlineData("retention", "{\"days\":61}", "config.days")]
    [InlineData("retention", "{\"from\":\"2026-02-30\"}", "config.from")]
    public void ExplicitInvalidValues_DoNotBecomeDefaults(string type, string json, string field)
    {
        var result = InsightConfigValidator.Validate(type, JsonSerializer.Deserialize<JsonElement>(json), Now);
        Assert.False(result.IsValid);
        Assert.Contains(field, result.Errors.Keys);
    }

    [Fact]
    public void RelativeStorage_RemainsRelative_WhileRunUsesOneCapturedClock()
    {
        var input = JsonSerializer.SerializeToElement(new { @event = " signup ", interval = " DAY " });
        var first = InsightConfigValidator.Validate(" TREND ", input, Now);
        Assert.True(first.IsValid);
        var storage = JsonSerializer.Deserialize<JsonElement>(first.StorageJson);
        Assert.False(storage.TryGetProperty("from", out _));
        Assert.False(storage.TryGetProperty("to", out _));
        Assert.Equal("signup", storage.GetProperty("event").GetString());
        Assert.Equal("day", storage.GetProperty("interval").GetString());
        var run = JsonSerializer.Deserialize<JsonElement>(first.RunJson);
        Assert.Equal(Now, run.GetProperty("to").GetDateTimeOffset());
        Assert.Equal(Now.AddDays(-30), run.GetProperty("from").GetDateTimeOffset());
        var tomorrow = InsightConfigValidator.Validate("trend", storage, Now.AddDays(1));
        Assert.Equal(first.StorageJson, tomorrow.StorageJson);
        Assert.Equal(Now.AddDays(1), JsonSerializer.Deserialize<JsonElement>(tomorrow.RunJson).GetProperty("to").GetDateTimeOffset());
    }

    [Fact]
    public void Limits_CountUtf8BytesAndEveryFunnelStep()
    {
        var large = JsonSerializer.SerializeToElement(new { @event = "signup", description = new string('é', 33000) });
        Assert.Contains("config", InsightConfigValidator.Validate("trend", large, Now).Errors.Keys);
        var tooMany = JsonSerializer.SerializeToElement(new { steps = Enumerable.Repeat("signup", 21) });
        Assert.Contains("config.steps", InsightConfigValidator.Validate("funnel", tooMany, Now).Errors.Keys);
        var maximum = JsonSerializer.SerializeToElement(new { steps = Enumerable.Repeat("signup", 20), windowDays = 90 });
        Assert.True(InsightConfigValidator.Validate("funnel", maximum, Now).IsValid);
        Assert.True(InsightConfigValidator.Validate("retention", JsonSerializer.SerializeToElement(new { }), Now).IsValid);
    }
}
