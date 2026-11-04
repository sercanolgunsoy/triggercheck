using System.Security.Cryptography;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Web.Api;

namespace TriggerCheck.Web.Agents;

/// <summary>
/// Probe agent credential: <c>tcr_&lt;prefix&gt;_&lt;secret&gt;</c> (prefix: 10 lower-case base32 chars, public and indexed;
/// secret: 32 random bytes, base64url). Only SHA-256(token) is stored, as for API keys (<see cref="ApiKeyTokens"/>); the
/// different marker keeps agent credentials and customer API keys from ever being accepted in each other's place.
/// </summary>
public static class AgentTokens
{
    private const int PrefixLength = 10;
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    public sealed record Generated(string Token, string Prefix, string Hash);

    public static Generated Generate()
    {
        var prefix = string.Concat(RandomNumberGenerator.GetBytes(PrefixLength).Select(b => Base32[b & 31]));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{AgentProtocol.TokenMarker}_{prefix}_{secret}";
        return new(token, prefix, ApiKeyTokens.Hash(token));
    }

    public static bool TryGetPrefix(string? token, out string prefix)
    {
        prefix = "";
        if (string.IsNullOrEmpty(token) || token.Length > 200) return false;
        var parts = token.Split('_', 3);
        if (parts.Length != 3 || parts[0] != AgentProtocol.TokenMarker || parts[1].Length != PrefixLength || parts[2].Length == 0) return false;
        if (!parts[1].All(Base32.Contains)) return false;
        prefix = parts[1];
        return true;
    }

    public static bool Matches(string token, string storedHash) => ApiKeyTokens.Matches(token, storedHash);

    public static string Display(string prefix) => $"{AgentProtocol.TokenMarker}_{prefix}_…";
}
