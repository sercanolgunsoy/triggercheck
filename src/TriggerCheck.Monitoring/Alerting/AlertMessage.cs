using System.Net;
using System.Text;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;

namespace TriggerCheck.Monitoring.Alerting;

public enum AlertKind
{
    Down,
    Up,
    /// <summary>"Send test notification" from the alert channels page (no monitor involved, MonitorId = 0).</summary>
    Test,
    /// <summary>The TLS certificate of an https monitor expires soon (FEAT-1). Independent of Down/Up.</summary>
    CertificateExpiring,
    /// <summary>
    /// FEAT-5: an incident is still open and unacknowledged after an escalation step's delay; sent only to that step's
    /// channels (<see cref="AlertMessage.ChannelIds"/>). Has its own lane in <see cref="AlertSenderService"/>.
    /// </summary>
    Escalation
}

/// <summary>A status-change alert for one monitor, a certificate-expiry warning, an escalation, or a channel test message.</summary>
/// <param name="IncidentId">Incident of a DOWN / escalation alert (acknowledge link, escalation checks).</param>
/// <param name="EscalationLevel">Escalation step (1-based) of an <see cref="AlertKind.Escalation"/> alert.</param>
/// <param name="ChannelIds">Restricts delivery to these channels of the customer (escalation steps); null = all enabled channels.</param>
/// <param name="AckUrl">Signed deep link to the acknowledge page, added by the dispatcher when configured.</param>
public sealed record AlertMessage(
    int MonitorId,
    int CustomerId,
    string MonitorName,
    string Target,
    AlertKind Kind,
    string? Error,
    DateTime OccurredAtUtc,
    TimeSpan? Downtime = null,
    DateTime? CertificateExpiresAtUtc = null,
    int? IncidentId = null,
    int EscalationLevel = 0,
    IReadOnlyList<int>? ChannelIds = null,
    string? AckUrl = null)
{
    public string StatusText => Kind switch
    {
        AlertKind.Down => "down",
        AlertKind.Up => "up",
        AlertKind.CertificateExpiring => "certificate_expiring",
        AlertKind.Escalation => "escalation",
        _ => "test"
    };

    /// <summary>Upper-case status for titles: DOWN / UP / TEST / CERTIFICATE EXPIRING / ESCALATION.</summary>
    public string StatusLabel => Kind == AlertKind.CertificateExpiring ? "CERTIFICATE EXPIRING" : StatusText.ToUpperInvariant();

    /// <summary>DOWN and escalation alerts: the monitor is (still) down.</summary>
    public bool IsDownLike => Kind is AlertKind.Down or AlertKind.Escalation;

    /// <summary>Whole days until <see cref="CertificateExpiresAtUtc"/> at <see cref="OccurredAtUtc"/> (never negative).</summary>
    public int? CertificateDaysLeft => CertificateExpiresAtUtc is { } e ? Math.Max(0, (int)Math.Floor((e - OccurredAtUtc).TotalDays)) : null;

    /// <summary>Single-line subject (CR/LF stripped so it is safe as a mail header).</summary>
    public string Subject => $"[TriggerCheck] {StatusLabel}: {OneLine(MonitorName)}";

    /// <summary>One-line summary used as title by chat channels, SMS and voice.</summary>
    public string Headline => Kind switch
    {
        AlertKind.Down => $"Monitor \"{OneLine(MonitorName)}\" is DOWN.",
        AlertKind.Up => $"Monitor \"{OneLine(MonitorName)}\" is UP again.",
        AlertKind.CertificateExpiring => CertificateDaysLeft is { } days
            ? $"The TLS certificate of monitor \"{OneLine(MonitorName)}\" expires in {days} day{(days == 1 ? "" : "s")}."
            : $"The TLS certificate of monitor \"{OneLine(MonitorName)}\" expires soon.",
        AlertKind.Escalation => $"Monitor \"{OneLine(MonitorName)}\" is still DOWN and nobody has acknowledged the incident (escalation level {EscalationLevel}).",
        _ => "This is a test notification from TriggerCheck."
    };

    /// <summary>
    /// Monitor target without credentials / query string (<see cref="SecretMasker.MaskUrl"/>), for chat, SMS and voice
    /// messages that end up in third-party systems.
    /// </summary>
    public string MaskedTarget => SecretMasker.MaskUrl(Target);

    /// <summary>Downtime as "1h 5m", or null.</summary>
    public string? DowntimeText => Kind is AlertKind.Up or AlertKind.Escalation && Downtime is { } d ? FormatDuration(d) : null;

    /// <summary>The acknowledge link, only on DOWN / escalation alerts.</summary>
    public string? AckLink => IsDownLike ? AckUrl : null;

    /// <summary>Plain-text body (e-mail).</summary>
    public string Body => BuildBody(Target);

    /// <summary>Plain-text body with <see cref="MaskedTarget"/> (third-party chat channels).</summary>
    public string MaskedBody => BuildBody(MaskedTarget);

    private string BuildBody(string target)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Headline);
        if (Kind == AlertKind.Test)
        {
            sb.AppendLine("If you can read this, the alert channel works.");
            sb.AppendLine($"Time (UTC): {OccurredAtUtc:yyyy-MM-dd HH:mm:ss}");
            return sb.ToString();
        }
        sb.AppendLine($"Target: {target}");
        sb.AppendLine($"Time (UTC): {OccurredAtUtc:yyyy-MM-dd HH:mm:ss}");
        if (IsDownLike && !string.IsNullOrEmpty(Error)) sb.AppendLine($"Error: {Error}");
        if (Kind == AlertKind.CertificateExpiring && CertificateExpiresAtUtc is { } exp)
            sb.AppendLine($"Certificate expires (UTC): {exp:yyyy-MM-dd HH:mm:ss}");
        if (DowntimeText is { } d) sb.AppendLine($"Downtime: {d}");
        if (AckLink is { } link) sb.AppendLine($"Acknowledge: {link}");
        return sb.ToString();
    }

    /// <summary>
    /// Escalation alert for an open incident that started at <paramref name="incidentStartedAtUtc"/>, delivered only to
    /// <paramref name="channelIds"/>.
    /// </summary>
    public static AlertMessage ForEscalation(Domain.Entities.Monitor monitor, int incidentId, DateTime incidentStartedAtUtc, int level,
        IReadOnlyList<int> channelIds, DateTime occurredAtUtc) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, AlertKind.Escalation, monitor.LastError,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), occurredAtUtc - incidentStartedAtUtc,
            IncidentId: incidentId, EscalationLevel: level, ChannelIds: channelIds);

    public static AlertMessage For(Domain.Entities.Monitor monitor, AlertKind kind, DateTime occurredAtUtc, TimeSpan? downtime = null) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, kind,
            kind == AlertKind.Down ? monitor.LastError : null,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), downtime);

    /// <summary>Warning that the monitor's TLS certificate (<see cref="Domain.Entities.Monitor.CertExpiresAtUtc"/>) expires soon.</summary>
    public static AlertMessage CertificateExpiring(Domain.Entities.Monitor monitor, DateTime occurredAtUtc) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, AlertKind.CertificateExpiring, null,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), null,
            monitor.CertExpiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null);

    /// <summary>The message sent by "Send test notification".</summary>
    public static AlertMessage TestFor(int customerId, DateTime occurredAtUtc) =>
        new(0, customerId, "Test notification", "", AlertKind.Test, null, DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc));

    internal static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h {d.Minutes}m"
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
        : $"{Math.Max(0, (int)d.TotalSeconds)}s";
}

/// <summary>
/// Outcome of one delivery attempt. <see cref="Retryable"/> marks transient failures (network error, timeout, HTTP 5xx /
/// 408 / 429) that the dispatcher retries with backoff; permanent ones (invalid target, other HTTP 4xx, not configured)
/// are not. <see cref="Error"/> is stored in Notification.Error and shown to the customer: never put secrets in it.
/// </summary>
public sealed record NotifyResult(bool Success, string? Error = null, bool Retryable = false)
{
    public static readonly NotifyResult Ok = new(true);
    public static NotifyResult Fail(string error) => new(false, error);
    public static NotifyResult Transient(string error) => new(false, error, Retryable: true);

    /// <summary>Non-2xx response of <paramref name="what"/>: 408 / 429 / 5xx are retried, other codes are not.</summary>
    public static NotifyResult ForHttpStatus(string what, HttpStatusCode status)
    {
        var code = (int)status;
        var error = $"{what} returned HTTP {code}";
        return code is 408 or 429 or >= 500 ? Transient(error) : Fail(error);
    }
}

/// <summary>Where one alert goes: the channel's address plus its secret (Telegram bot token), if any.</summary>
public sealed record NotifyTarget(string Address, string? Secret = null)
{
    public static implicit operator NotifyTarget(string address) => new(address);

    /// <summary>Never print the secret (records include every property in ToString by default).</summary>
    public override string ToString() => Address;
}

/// <summary>Delivers an alert over one <see cref="AlertChannelType"/>. Exceptions are treated as transient failures.</summary>
public interface INotifier
{
    AlertChannelType Type { get; }

    /// <summary>
    /// Whether repeating a request whose outcome is unknown (timeout, exception) is harmless. Non-idempotent channels
    /// (SMS / voice: a repeat costs money and pages twice) are only retried when the request surely was not processed.
    /// </summary>
    bool IsIdempotent => true;

    Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct);
}
