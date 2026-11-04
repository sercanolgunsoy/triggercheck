using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Monitoring.Escalation;

/// <summary>Escalation settings (<c>Escalation</c> config section, FEAT-5).</summary>
public class EscalationOptions
{
    public const string Section = "Escalation";

    /// <summary>How long an acknowledge link stays valid.</summary>
    public int AckLinkValidHours { get; set; } = 72;
    /// <summary>How often the escalation job looks for unacknowledged incidents.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;
}

/// <summary>A verified acknowledge-link token.</summary>
public sealed record AckToken(int IncidentId, int CustomerId);

/// <summary>
/// Acknowledge links in DOWN / escalation alerts (FEAT-5). The token is the incident id and customer id protected with
/// ASP.NET Core Data Protection (<see cref="ITimeLimitedDataProtector"/>, purpose <see cref="Purpose"/>): authenticated
/// encryption bound to one incident and one customer, with an expiry. Keys come from the app's key ring (persisted under
/// /data in the container, so links survive restarts). The link only deep-links to the confirmation page
/// (<see cref="AckPath"/>): using it still requires signing in as an admin of that customer and confirming with a POST,
/// so a leaked link alone cannot acknowledge anything.
/// The absolute URL is built from <c>Site:PublicBaseUrl</c>; without it alerts carry no link.
/// </summary>
public sealed class IncidentAckLinks
{
    public const string Purpose = "IncidentAck.v1";
    public const string AckPath = "/App/Incidents/Ack";
    public const string BaseUrlSetting = "Site:PublicBaseUrl";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly string? _baseUrl;
    private readonly TimeSpan _validity;

    public IncidentAckLinks(IDataProtectionProvider dataProtection, IConfiguration config, IOptions<EscalationOptions> options, ILogger<IncidentAckLinks> logger)
    {
        _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _validity = TimeSpan.FromHours(Math.Clamp(options.Value.AckLinkValidHours, 1, 24 * 30));
        _baseUrl = ParseBaseUrl(config[BaseUrlSetting]);
        if (_baseUrl is null && !string.IsNullOrWhiteSpace(config[BaseUrlSetting]))
            logger.LogWarning("{Setting} is not an absolute http(s) URL; alerts carry no acknowledge link", BaseUrlSetting);
    }

    /// <summary>The configured public base URL without a trailing slash, or null when unset / not an absolute http(s) URL.</summary>
    public static string? ParseBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;

    /// <summary>
    /// Wall-clock expiry on purpose: the time-limited protector checks it against the system clock, not a TimeProvider.
    /// </summary>
    public string CreateToken(int incidentId, int customerId) =>
        _protector.Protect(Payload(incidentId, customerId), DateTimeOffset.UtcNow.Add(_validity));

    public static string Payload(int incidentId, int customerId) => string.Create(CultureInfo.InvariantCulture, $"{incidentId}.{customerId}");

    /// <summary>The acknowledge link for the incident, or null when no public base URL is configured.</summary>
    public string? CreateUrl(int incidentId, int customerId) =>
        _baseUrl is null ? null : $"{_baseUrl}{AckPath}?token={Uri.EscapeDataString(CreateToken(incidentId, customerId))}";

    /// <summary>False for tampered, foreign-key, malformed or expired tokens (indistinguishable by design).</summary>
    public bool TryValidate(string? token, out AckToken? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        string payload;
        try { payload = _protector.Unprotect(token, out _); }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        var parts = payload.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var incidentId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var customerId))
            return false;
        result = new AckToken(incidentId, customerId);
        return true;
    }
}
