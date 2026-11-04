namespace TriggerCheck.Domain.Entities;

public class Monitor
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string Name { get; set; } = "";
    public MonitorType Type { get; set; } = MonitorType.Http;

    /// <summary>For Http/Keyword: absolute URL. For Tcp/Ping: host name or IP.</summary>
    public string Target { get; set; } = "";
    /// <summary>Only for Tcp.</summary>
    public int? Port { get; set; }
    /// <summary>Only for Keyword: text that must appear in the response body.</summary>
    public string? Keyword { get; set; }
    /// <summary>Http/Keyword: comma separated accepted status codes / ranges, e.g. "200-299,301".</summary>
    public string ExpectedStatusCodes { get; set; } = "200-299";
    public string HttpMethod { get; set; } = "GET";

    /// <summary>Check interval. Must be &gt;= Customer.Plan.MinIntervalSeconds.</summary>
    public int IntervalSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Number of consecutive failed checks before the monitor is marked Down (avoids flapping).</summary>
    public int FailureThreshold { get; set; } = 2;

    public bool IsEnabled { get; set; } = true;

    // ---- runtime state, maintained by the monitoring engine ----
    public MonitorStatus Status { get; set; } = MonitorStatus.Unknown;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? NextCheckAtUtc { get; set; }
    public int? LastResponseTimeMs { get; set; }
    public string? LastError { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }
    /// <summary>
    /// Set when the monitor was paused while it was Down (its incident is closed on pause). The first successful
    /// check after resuming then sends an UP alert, so subscribers are not left with a DOWN as the last word.
    /// </summary>
    public bool DownWhenPaused { get; set; }

    /// <summary>
    /// Application-managed optimistic concurrency token (SQLite has no rowversion). Bumped by every user-initiated
    /// change (edit, pause, resume); the check runner never bumps it, but its save fails if it changed meanwhile.
    /// </summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    public ICollection<Incident> Incidents { get; set; } = new List<Incident>();
}
