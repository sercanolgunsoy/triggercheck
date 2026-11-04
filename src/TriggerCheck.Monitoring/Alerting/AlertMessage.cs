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
    Test
}

/// <summary>A status-change alert for one monitor (or a channel test message).</summary>
public sealed record AlertMessage(
    int MonitorId,
    int CustomerId,
    string MonitorName,
    string Target,
    AlertKind Kind,
    string? Error,
    DateTime OccurredAtUtc,
    TimeSpan? Downtime = null)
{
    public string StatusText => Kind switch { AlertKind.Down => "down", AlertKind.Up => "up", _ => "test" };

    /// <summary>Upper-case status for titles: DOWN / UP / TEST.</summary>
    public string StatusLabel => StatusText.ToUpperInvariant();

    /// <summary>Single-line subject (CR/LF stripped so it is safe as a mail header).</summary>
    public string Subject => $"[TriggerCheck] {StatusLabel}: {OneLine(MonitorName)}";

    /// <summary>One-line summary used as title by chat channels, SMS and voice.</summary>
    public string Headline => Kind switch
    {
        AlertKind.Down => $"Monitor \"{OneLine(MonitorName)}\" is DOWN.",
        AlertKind.Up => $"Monitor \"{OneLine(MonitorName)}\" is UP again.",
        _ => "This is a test notification from TriggerCheck."
    };

    /// <summary>
    /// Monitor target without credentials / query string (<see cref="SecretMasker.MaskUrl"/>), for chat, SMS and voice
    /// messages that end up in third-party systems.
    /// </summary>
    public string MaskedTarget => SecretMasker.MaskUrl(Target);

    /// <summary>Downtime as "1h 5m", or null.</summary>
    public string? DowntimeText => Kind == AlertKind.Up && Downtime is { } d ? FormatDuration(d) : null;

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
        if (Kind == AlertKind.Down && !string.IsNullOrEmpty(Error)) sb.AppendLine($"Error: {Error}");
        if (DowntimeText is { } d) sb.AppendLine($"Downtime: {d}");
        return sb.ToString();
    }

    public static AlertMessage For(Domain.Entities.Monitor monitor, AlertKind kind, DateTime occurredAtUtc, TimeSpan? downtime = null) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, kind,
            kind == AlertKind.Down ? monitor.LastError : null,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), downtime);

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
