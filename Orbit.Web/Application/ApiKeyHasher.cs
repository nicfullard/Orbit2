using System.Security.Cryptography;
using System.Text;

namespace Orbit.Application;

public static class ApiKeyHasher
{
    public const string KeyPrefix = "orbit_";
    // Orbit Agent credentials (spec §8.2). Neither starts with "orbit_", so an agent secret is never even
    // looked up as an API key, and vice versa.
    public const string AgentSecretPrefix = "orbitagent_";
    public const string AgentRegistrationTokenPrefix = "orbitreg_";

    /// <summary>Generates a new raw key. Only the hash is stored; the raw value is shown once.</summary>
    public static string GenerateRawKey() => GenerateRawKey(KeyPrefix);

    public static string GenerateRawKey(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return prefix + Base64UrlEncode(bytes);
    }

    public static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey.Trim()));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
