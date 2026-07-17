using System.Security.Cryptography;
using System.Text;

namespace Pulse.Domain;

/// <summary>
/// Generates the three kinds of API keys:
/// <c>pk_live_…</c> project write keys (capture only),
/// <c>rk_live_…</c> project read keys (query endpoints only), and
/// <c>pk_user_…</c> personal keys (full management API as a user).
/// </summary>
public static class ApiKeyGenerator
{
    public const string Prefix = "pk_live_";
    public const string ReadPrefix = "rk_live_";
    public const string PersonalPrefix = "pk_user_";

    /// <summary>Creates a new 32-hex-char write key with the <c>pk_live_</c> prefix.</summary>
    public static string NewKey() => NewKey(Prefix);

    /// <summary>Creates a new read key with the <c>rk_live_</c> prefix.</summary>
    public static string NewReadKey() => NewKey(ReadPrefix);

    /// <summary>Creates a new personal API key with the <c>pk_user_</c> prefix.</summary>
    public static string NewPersonalKey() => NewKey(PersonalPrefix);

    /// <summary>SHA-256 of a key as lowercase hex — how personal keys are stored.</summary>
    public static string Sha256(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string NewKey(string prefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToHexStringLower(bytes);
    }
}
