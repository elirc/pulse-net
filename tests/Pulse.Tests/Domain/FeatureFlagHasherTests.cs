using Pulse.Domain;

namespace Pulse.Tests.Domain;

public class FeatureFlagHasherTests
{
    [Fact]
    public void Rank_IsDeterministic()
    {
        var first = FeatureFlagHasher.Rank("new-onboarding", "user-1");
        var second = FeatureFlagHasher.Rank("new-onboarding", "user-1");

        Assert.Equal(first, second);
        Assert.InRange(first, 0.0, 1.0);
    }

    [Fact]
    public void Rank_DiffersAcrossFlagsAndUsers()
    {
        var baseline = FeatureFlagHasher.Rank("flag-a", "user-1");

        Assert.NotEqual(baseline, FeatureFlagHasher.Rank("flag-b", "user-1"));
        Assert.NotEqual(baseline, FeatureFlagHasher.Rank("flag-a", "user-2"));
        Assert.NotEqual(baseline, FeatureFlagHasher.Rank("flag-a", "user-1", FeatureFlagHasher.VariantSalt));
    }

    [Fact]
    public void IsInRollout_100PercentIsAlwaysOn_0PercentAlwaysOff()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.True(FeatureFlagHasher.IsInRollout("any-flag", $"user-{i}", 100));
            Assert.False(FeatureFlagHasher.IsInRollout("any-flag", $"user-{i}", 0));
        }
    }

    [Fact]
    public void IsInRollout_50Percent_SplitsUsersRoughlyInHalf()
    {
        var on = Enumerable.Range(0, 1000)
            .Count(i => FeatureFlagHasher.IsInRollout("split-test", $"user-{i}", 50));

        Assert.InRange(on, 400, 600);
    }

    [Fact]
    public void PickVariant_IsDeterministic_AndRespectsWeights()
    {
        List<(string, double)> variants = [("control", 50), ("test", 50)];

        var assignments = Enumerable.Range(0, 1000)
            .Select(i => FeatureFlagHasher.PickVariant("ab-test", $"user-{i}", variants))
            .ToList();

        // Deterministic.
        Assert.Equal(
            assignments[0],
            FeatureFlagHasher.PickVariant("ab-test", "user-0", variants));

        // Roughly even split.
        Assert.InRange(assignments.Count(v => v == "control"), 400, 600);

        // Skewed weights skew the split.
        var skewed = Enumerable.Range(0, 1000)
            .Count(i => FeatureFlagHasher.PickVariant(
                "skewed", $"user-{i}", [("rare", 10), ("common", 90)]) == "rare");
        Assert.InRange(skewed, 50, 170);
    }
}
