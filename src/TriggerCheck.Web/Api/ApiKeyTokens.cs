using System.Security.Cryptography;
using System.Text;

namespace TriggerCheck.Web.Api;

/// <summary>
/// API key token format: <c>tc_&lt;prefix&gt;_&lt;secret&gt;</c>. The prefix (10 lower-case base32 chars) is public and
/// indexed; the secret is 32 random bytes (base64url). Only SHA-256(token) is stored: the token is high-entropy random
/// data, so a fast hash is sufficient (no password stretching needed).
/// </summary>
public static class ApiKeyTokens
{
    public const string Marker = "tc";
    private const int PrefixLength = 10;
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    public sealed record Generated(string Token, string Prefix, string Hash);

    public static Generated Generate()
    {
        var prefix = string.Concat(RandomNumberGenerator.GetBytes(PrefixLength).Select(b => Base32[b & 31]));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{Marker}_{prefix}_{secret}";
        return new(token, prefix, Hash(token));
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Extracts the prefix of a well-formed token.</summary>
    public static bool TryGetPrefix(string? token, out string prefix)
    {
        prefix = "";
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var parts = token.Split('_', 3);
        if (parts.Length != 3 || parts[0] != Marker || parts[1].Length != PrefixLength || parts[2].Length == 0) return false;
        if (!parts[1].All(Base32.Contains)) return false;
        prefix = parts[1];
        return true;
    }

    /// <summary>Constant-time comparison of the token's hash with the stored one.</summary>
    public static bool Matches(string token, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(storedHash));

    /// <summary>What the UI shows to identify a key (the secret part is never stored).</summary>
    public static string Display(string prefix) => $"{Marker}_{prefix}_…";
}
