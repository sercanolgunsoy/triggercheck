using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Escalation;

/// <summary>Escalation and acknowledge-link settings (<c>Escalation</c> config section, FEAT-5).</summary>
public class EscalationOptions
{
    public const string Section = "Escalation";

    /// <summary>
    /// Public base URL of the web app (e.g. https://monitor.example.com), used for the acknowledge links in DOWN and
    /// escalation alerts. Empty = alerts carry no link (incidents can still be acknowledged in the panel).
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";
    /// <summary>
    /// HMAC key of the acknowledge links (at least 32 characters; set it via user secrets / environment, never in
    /// committed config). Empty = a random key per process: links then stop working after a restart.
    /// </summary>
    public string AckLinkSecret { get; set; } = "";
    /// <summary>How long an acknowledge link stays valid.</summary>
    public int AckLinkValidHours { get; set; } = 72;
    /// <summary>How often the escalation job looks for unacknowledged incidents.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;
}

public enum AckTokenStatus
{
    Valid,
    /// <summary>Malformed or the signature does not match (tampered, or signed with another key).</summary>
    Invalid,
    Expired
}

/// <summary>A verified acknowledge-link token.</summary>
public sealed record AckToken(int IncidentId, int CustomerId, DateTime ExpiresAtUtc);

/// <summary>
/// Signed one-click acknowledge links (FEAT-5). Token = <c>{incidentId}.{customerId}.{expiresUnix}.{signature}</c> with
/// an HMAC-SHA256 signature (base64url) over the first three parts, so the token is bound to one incident and one
/// customer and expires. The link only deep-links to the confirmation page (/App/Incidents/Ack): using it still requires
/// signing in as an admin of that customer and confirming with a POST, so a leaked link alone cannot acknowledge.
/// </summary>
public sealed class IncidentAckLinks
{
    public const string AckPath = "/App/Incidents/Ack";

    private readonly byte[] _key;
    private readonly string? _baseUrl;
    private readonly TimeSpan _validity;
    private readonly TimeProvider _time;

    public IncidentAckLinks(IOptions<EscalationOptions> options, TimeProvider time, ILogger<IncidentAckLinks> logger)
    {
        var o = options.Value;
        _time = time;
        _validity = TimeSpan.FromHours(Math.Clamp(o.AckLinkValidHours, 1, 24 * 30));
        if (string.IsNullOrEmpty(o.AckLinkSecret) || o.AckLinkSecret.Length < 32)
        {
            if (!string.IsNullOrEmpty(o.AckLinkSecret))
                logger.LogWarning("Escalation:AckLinkSecret is shorter than 32 characters and is ignored");
            _key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(o.AckLinkSecret);
        }

        if (Uri.TryCreate(o.PublicBaseUrl?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            _baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        else if (!string.IsNullOrWhiteSpace(o.PublicBaseUrl))
            logger.LogWarning("Escalation:PublicBaseUrl is not an absolute http(s) URL; alerts carry no acknowledge link");
    }

    public string CreateToken(int incidentId, int customerId)
    {
        var expires = _time.GetUtcNow().Add(_validity).ToUnixTimeSeconds();
        var payload = string.Create(CultureInfo.InvariantCulture, $"{incidentId}.{customerId}.{expires}");
        return payload + "." + Sign(payload);
    }

    /// <summary>The acknowledge link for the incident, or null when no public base URL is configured.</summary>
    public string? CreateUrl(int incidentId, int customerId) =>
        _baseUrl is null ? null : $"{_baseUrl}{AckPath}?token={Uri.EscapeDataString(CreateToken(incidentId, customerId))}";

    /// <summary>Verifies signature first, then expiry.</summary>
    public AckTokenStatus Validate(string? token, out AckToken? result)
    {
        result = null;
        var parts = token?.Split('.');
        if (parts is not { Length: 4 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var incidentId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var customerId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
            return AckTokenStatus.Invalid;

        byte[] given;
        try { given = WebEncoders.Base64UrlDecode(parts[3]); }
        catch (FormatException) { return AckTokenStatus.Invalid; }
        var expected = Mac(string.Join('.', parts[0], parts[1], parts[2]));
        if (!CryptographicOperations.FixedTimeEquals(given, expected)) return AckTokenStatus.Invalid;

        DateTime expiresAt;
        try { expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return AckTokenStatus.Invalid; }
        if (expiresAt <= _time.GetUtcNow().UtcDateTime) return AckTokenStatus.Expired;
        result = new AckToken(incidentId, customerId, expiresAt);
        return AckTokenStatus.Valid;
    }

    private string Sign(string payload) => WebEncoders.Base64UrlEncode(Mac(payload));

    private byte[] Mac(string payload) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
}
