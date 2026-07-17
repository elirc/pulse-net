using Pulse.Domain;

namespace Pulse.Tests.Domain;

/// <summary>
/// Boundary and stability properties of the deterministic rollout hash that
/// the /decide endpoint relies on.
/// </summary>
public class FlagRolloutBoundaryTests
{
    [Fact]
    public void IsInRollout_IsMonotonic_RaisingThePercentageNeverDropsAUser()
    {
        // Every user on at 25% must stay on at 50% and 75% — increasing a
        // rollout only ever adds users, it never reshuffles them.
        for (var i = 0; i < 500; i++)
        {
            var user = $"user-{i}";
            if (FeatureFlagHasher.IsInRollout("ramp-up", user, 25))
            {
                Assert.True(FeatureFlagHasher.IsInRollout("ramp-up", user, 50));
                Assert.True(FeatureFlagHasher.IsInRollout("ramp-up", user, 75));
            }

            if (FeatureFlagHasher.IsInRollout("ramp-up", user, 50))
            {
                Assert.True(FeatureFlagHasher.IsInRollout("ramp-up", user, 75));
            }
        }
    }

    [Fact]
    public void IsInRollout_UserExactlyAtTheThreshold_IsExcluded()
    {
        // The gate is strict-less-than: a user whose rank lands exactly on
        // the rollout percentage is out.
        var rank = FeatureFlagHasher.Rank("edge-flag", "edge-user");
        var exactPercentage = rank * 100;

        Assert.False(FeatureFlagHasher.IsInRollout("edge-flag", "edge-user", exactPercentage));

        // Nudge the rollout just above the rank and the user flips on.
        Assert.True(FeatureFlagHasher.IsInRollout("edge-flag", "edge-user", exactPercentage + 1e-9));
    }

    [Fact]
    public void IsInRollout_100Percent_ShortCircuitsForEveryUser()
    {
        // 100% bypasses the hash entirely, so even a rank of exactly 1.0
        // could not be excluded.
        for (var i = 0; i < 100; i++)
        {
            Assert.True(FeatureFlagHasher.IsInRollout("full-flag", $"user-{i}", 100));
        }
    }

    [Fact]
    public void PickVariant_ZeroWeightVariant_IsNeverChosen()
    {
        List<(string, double)> variants = [("never", 0), ("always", 100)];

        for (var i = 0; i < 500; i++)
        {
            Assert.Equal("always", FeatureFlagHasher.PickVariant("zero-weight", $"user-{i}", variants));
        }
    }

    [Fact]
    public void PickVariant_SingleVariantAt100_AlwaysWins()
    {
        List<(string, double)> variants = [("only", 100)];

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal("only", FeatureFlagHasher.PickVariant("single", $"user-{i}", variants));
        }
    }

    [Fact]
    public void PickVariant_CumulativeBoundaries_PartitionUsersCompletely()
    {
        // Three-way split: every user gets exactly one variant, and each
        // variant's share tracks its weight.
        List<(string, double)> variants = [("a", 20), ("b", 30), ("c", 50)];

        var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
        for (var i = 0; i < 2000; i++)
        {
            counts[FeatureFlagHasher.PickVariant("three-way", $"user-{i}", variants)]++;
        }

        Assert.Equal(2000, counts.Values.Sum());
        Assert.InRange(counts["a"], 300, 500);   // ~400
        Assert.InRange(counts["b"], 480, 720);   // ~600
        Assert.InRange(counts["c"], 850, 1150);  // ~1000
    }

    [Fact]
    public void PickVariant_UsesTheVariantSalt_SoAssignmentIsIndependentOfTheRolloutGate()
    {
        // If variant selection reused the rollout rank, users inside a 50%
        // rollout would all land in the first half of the variant space.
        List<(string, double)> variants = [("first", 50), ("second", 50)];

        var insideRollout = Enumerable.Range(0, 2000)
            .Select(i => $"user-{i}")
            .Where(u => FeatureFlagHasher.IsInRollout("salted", u, 50))
            .ToList();

        var second = insideRollout.Count(
            u => FeatureFlagHasher.PickVariant("salted", u, variants) == "second");

        // Roughly half of the rolled-out users still get the second variant.
        Assert.InRange((double)second / insideRollout.Count, 0.35, 0.65);
    }

    [Fact]
    public void Rank_MatchesTheDocumentedHashRecipe_AcrossProcessRestarts()
    {
        // Golden value: SHA-256("new-onboarding.user-1") is stable across
        // machines, runs and deploys, so a user's bucket can never drift. If
        // this test breaks, every existing rollout reshuffles.
        var rank = FeatureFlagHasher.Rank("new-onboarding", "user-1");

        Assert.Equal(0.11373540990598746, rank, precision: 15);

        // The rank must be sensitive to every part of the key material.
        Assert.NotEqual(rank, FeatureFlagHasher.Rank("new-onboarding", "user-1 "));
        Assert.NotEqual(rank, FeatureFlagHasher.Rank("New-Onboarding", "user-1"));
    }
}
