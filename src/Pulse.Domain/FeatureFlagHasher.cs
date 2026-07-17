using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pulse.Domain;

/// <summary>
/// Deterministic rollout bucketing. Hashing <c>flagKey.distinctId</c> gives
/// every (flag, user) pair a stable position in [0, 1]; a flag at N% rollout
/// is on for users whose position is below N/100. Variant selection uses a
/// different salt so the variant split is independent of the rollout gate.
/// </summary>
public static class FeatureFlagHasher
{
    public const string VariantSalt = ".variant";

    /// <summary>Stable position of this (flag, user) pair in [0, 1].</summary>
    public static double Rank(string flagKey, string distinctId, string salt = "")
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{flagKey}.{distinctId}{salt}"));
        var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        return value / (double)ulong.MaxValue;
    }

    /// <summary>True when the pair falls inside the rollout percentage.</summary>
    public static bool IsInRollout(string flagKey, string distinctId, double rolloutPercentage) =>
        rolloutPercentage >= 100
        || Rank(flagKey, distinctId) * 100 < rolloutPercentage;

    /// <summary>
    /// Picks a variant by walking the cumulative rollout percentages with the
    /// variant-salted rank. Assumes percentages sum to 100 (validated at save).
    /// </summary>
    public static string PickVariant(
        string flagKey,
        string distinctId,
        IReadOnlyList<(string Key, double RolloutPercentage)> variants)
    {
        var position = Rank(flagKey, distinctId, VariantSalt) * 100;

        double cumulative = 0;
        foreach (var (key, rollout) in variants)
        {
            cumulative += rollout;
            if (position < cumulative)
            {
                return key;
            }
        }

        return variants[^1].Key; // Floating-point safety net.
    }
}
