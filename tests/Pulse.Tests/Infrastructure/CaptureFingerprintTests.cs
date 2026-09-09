using Pulse.Infrastructure.Services;

namespace Pulse.Tests.Infrastructure;

public class CaptureFingerprintTests
{
    [Theory]
    [InlineData("{\"a\":1,\"b\":{\"c\":2,\"d\":3}}", "{\"b\":{\"d\":3,\"c\":2},\"a\":1}", true)]
    [InlineData("{\"a\":1}", "{\"a\":1.0}", false)]
    [InlineData("{\"a\":1}", "{\"a\":\"1\"}", false)]
    [InlineData("{\"a\":[1,2]}", "{\"a\":[2,1]}", false)]
    [InlineData("{\"a\":null}", "{}", false)]
    [InlineData("{\"a\":true}", "{\"a\":false}", false)]
    [InlineData("{\"a\":\"日本語\"}", "{\"a\":\"\\u65e5\\u672c\\u8a9e\"}", true)]
    public void V1_RespectsObjectOrderArrayOrderAndScalarRepresentation(string a, string b, bool equivalent)
    {
        Assert.True(CaptureFingerprint.TryCreate(" event ", " user ", null, a, out var left));
        Assert.True(CaptureFingerprint.TryCreate("event", "user", null, b, out var right));
        Assert.Equal(equivalent, left == right);
    }
    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"items\":[{\"a\":1,\"a\":2}]}")]
    [InlineData("{\"a\":{\"b\":1,\"b\":1}}")]
    public void DuplicateObjectMembers_AreRejectedRecursively(string json) => Assert.False(CaptureFingerprint.TryCreate("e", "p", null, json, out _));
    [Fact]
    public void Timestamp_UsesUtcInstantAndExplicitOmittedMarker()
    {
        Assert.True(CaptureFingerprint.TryCreate("e", "p", DateTimeOffset.Parse("2026-03-01T10:00:00+02:00"), "{}", out var a));
        Assert.True(CaptureFingerprint.TryCreate("e", "p", DateTimeOffset.Parse("2026-03-01T08:00:00Z"), "{}", out var b));
        Assert.True(CaptureFingerprint.TryCreate("e", "p", null, "{}", out var omitted));
        Assert.Equal(a, b); Assert.NotEqual(a, omitted);
    }
}
