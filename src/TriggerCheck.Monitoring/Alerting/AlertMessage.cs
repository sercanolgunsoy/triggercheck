using System.Text;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Alerting;

public enum AlertKind { Down, Up, CertificateExpiring }

/// <summary>A status-change alert for one monitor.</summary>
public sealed record AlertMessage(
    int MonitorId,
    int CustomerId,
    string MonitorName,
    string Target,
    AlertKind Kind,
    string? Error,
    DateTime OccurredAtUtc,
    TimeSpan? Downtime = null,
    DateTime? CertificateExpiresAtUtc = null)
{
    public string StatusText => Kind switch
    {
        AlertKind.Down => "down",
        AlertKind.CertificateExpiring => "certificate_expiring",
        _ => "up"
    };

    private string KindTitle => Kind switch
    {
        AlertKind.Down => "DOWN",
        AlertKind.CertificateExpiring => "CERTIFICATE EXPIRING",
        _ => "UP"
    };

    /// <summary>Single-line subject (CR/LF stripped so it is safe as a mail header).</summary>
    public string Subject => $"[TriggerCheck] {KindTitle}: {OneLine(MonitorName)}";

    public string Body
    {
        get
        {
            var sb = new StringBuilder();
            if (Kind == AlertKind.CertificateExpiring)
            {
                sb.AppendLine($"The TLS certificate of monitor \"{MonitorName}\" expires soon.");
                sb.AppendLine($"Target: {Target}");
                if (CertificateExpiresAtUtc is { } exp)
                    sb.AppendLine($"Expires (UTC): {exp:yyyy-MM-dd HH:mm:ss} ({Math.Max(0, (int)Math.Floor((exp - OccurredAtUtc).TotalDays))} days left)");
                return sb.ToString();
            }
            sb.AppendLine(Kind == AlertKind.Down
                ? $"Monitor \"{MonitorName}\" is DOWN."
                : $"Monitor \"{MonitorName}\" is UP again.");
            sb.AppendLine($"Target: {Target}");
            sb.AppendLine($"Time (UTC): {OccurredAtUtc:yyyy-MM-dd HH:mm:ss}");
            if (Kind == AlertKind.Down && !string.IsNullOrEmpty(Error)) sb.AppendLine($"Error: {Error}");
            if (Kind == AlertKind.Up && Downtime is { } d) sb.AppendLine($"Downtime: {FormatDuration(d)}");
            return sb.ToString();
        }
    }

    public static AlertMessage For(Domain.Entities.Monitor monitor, AlertKind kind, DateTime occurredAtUtc, TimeSpan? downtime = null) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, kind,
            kind == AlertKind.Down ? monitor.LastError : null,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), downtime);

    /// <summary>One-time warning that the monitor's TLS certificate (<see cref="Domain.Entities.Monitor.CertExpiresAtUtc"/>) expires soon.</summary>
    public static AlertMessage CertificateExpiring(Domain.Entities.Monitor monitor, DateTime occurredAtUtc) =>
        new(monitor.Id, monitor.CustomerId, monitor.Name, monitor.Target, AlertKind.CertificateExpiring, null,
            DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), null,
            monitor.CertExpiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null);

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static string FormatDuration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h {d.Minutes}m"
        : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s"
        : $"{Math.Max(0, (int)d.TotalSeconds)}s";
}

public sealed record NotifyResult(bool Success, string? Error = null)
{
    public static readonly NotifyResult Ok = new(true);
    public static NotifyResult Fail(string error) => new(false, error);
}

/// <summary>Delivers an alert over one <see cref="AlertChannelType"/>.</summary>
public interface INotifier
{
    AlertChannelType Type { get; }
    /// <param name="target">Email address or webhook URL.</param>
    Task<NotifyResult> SendAsync(string target, AlertMessage message, CancellationToken ct);
}
