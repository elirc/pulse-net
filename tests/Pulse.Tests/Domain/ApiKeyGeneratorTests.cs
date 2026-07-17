using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class ApiKeyGeneratorTests
{
    [Fact]
    public void NewKey_HasExpectedPrefixAndLength()
    {
        var key = ApiKeyGenerator.NewKey();

        Assert.StartsWith(ApiKeyGenerator.Prefix, key);
        Assert.Equal(ApiKeyGenerator.Prefix.Length + 32, key.Length);
    }

    [Fact]
    public void NewKey_BodyIsLowercaseHex()
    {
        var body = ApiKeyGenerator.NewKey()[ApiKeyGenerator.Prefix.Length..];

        Assert.All(body, c => Assert.True(
            c is >= '0' and <= '9' or >= 'a' and <= 'f',
            $"Unexpected character '{c}' in key body."));
    }

    [Fact]
    public void NewKey_GeneratesUniqueKeys()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => ApiKeyGenerator.NewKey()).ToHashSet();

        Assert.Equal(200, keys.Count);
    }
}
