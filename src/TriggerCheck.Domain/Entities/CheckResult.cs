namespace TriggerCheck.Domain.Entities;

/// <summary>One single probe execution.</summary>
public class CheckResult
{
    public long Id { get; set; }
    public int MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public DateTime CheckedAtUtc { get; set; }
    public bool IsUp { get; set; }
    public int? StatusCode { get; set; }
    public int ResponseTimeMs { get; set; }
    public string? Error { get; set; }
    /// <summary>Confirmation probe of an earlier failure (confirm before alert). Excluded from uptime and chart statistics.</summary>
    public bool IsConfirmation { get; set; }

    // ---- HTTP/Keyword phase breakdown (ms). All null for Tcp/Ping and for probes that failed before the phase. ----
    // A probe that reused a pooled connection has no DNS/connect/TLS phase: DnsMs, ConnectMs and TlsMs are null
    // while TtfbMs is set. A new plain-http connection has DnsMs/ConnectMs but TlsMs null.

    /// <summary>DNS resolution time of the new connection (0 for literal IPs).</summary>
    public int? DnsMs { get; set; }
    /// <summary>TCP connect time of the new connection.</summary>
    public int? ConnectMs { get; set; }
    /// <summary>TLS handshake time of the new https connection.</summary>
    public int? TlsMs { get; set; }
    /// <summary>Time from the start of the probe to the response headers (includes DNS/connect/TLS when a new connection was opened).</summary>
    public int? TtfbMs { get; set; }
}
