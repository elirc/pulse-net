using System.Security.Cryptography;

namespace Pulse.Domain;

/// <summary>Generates project write keys, e.g. <c>pk_live_5f4dcc3b5aa7...</c>.</summary>
public static class ApiKeyGenerator
{
    public const string Prefix = "pk_live_";

    /// <summary>Creates a new 32-hex-char key with the <c>pk_live_</c> prefix.</summary>
    public static string NewKey()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Convert.ToHexStringLower(bytes);
    }
}
