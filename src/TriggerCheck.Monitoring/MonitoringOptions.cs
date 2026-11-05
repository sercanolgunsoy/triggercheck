namespace TriggerCheck.Monitoring;

public class MonitoringOptions
{
    public const string Section = "Monitoring";
    /// <summary>Run the scheduler in this process. Disable to run only the web UI (e.g. in tests).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often the scheduler looks for due monitors.</summary>
    public int SchedulerTickMilliseconds { get; set; } = 1000;
    /// <summary>Max concurrent probes.</summary>
    public int MaxConcurrency { get; set; } = 50;
    /// <summary>How often old CheckResults are purged according to plan retention.</summary>
    public int RetentionCleanupMinutes { get; set; } = 60;
    /// <summary>SSRF guard off: probes and webhooks may reach loopback / private / link-local addresses. Development and tests only.</summary>
    public bool AllowPrivateTargets { get; set; }
    /// <summary>
    /// Confirm before alert: when a failed probe would take a monitor DOWN, record it and schedule one more probe
    /// <see cref="ConfirmationDelaySeconds"/> later before opening an incident (no run waits). See MonitorCheckRunner.
    /// </summary>
    public bool ConfirmBeforeDown { get; set; } = true;
    /// <summary>Delay before the confirmation probe (0-300 s); the confirmation runs on the first scheduler tick after it.</summary>
    public int ConfirmationDelaySeconds { get; set; } = 5;

    /// <summary>
    /// OPS-3: identity of this instance in scheduler and job leases. Must be unique per running process; empty =
    /// machine name + process id + a random suffix (see <see cref="Cluster.InstanceIdentity"/>).
    /// </summary>
    public string? InstanceId { get; set; }
    /// <summary>
    /// OPS-3: slack added to a monitor's scheduler lease beyond the worst-case run time (gate wait + probe timeout,
    /// see <see cref="Scheduling.MonitorLease"/>) before another instance may reclaim it.
    /// </summary>
    public int SchedulerLeaseMarginSeconds { get; set; } = 30;

    /// <summary>Resolvers used when <see cref="DnsResolvers"/> is not configured.</summary>
    public static readonly string[] DefaultDnsResolvers = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// Recursive resolvers queried by DNS monitors, in order (the next one is tried when one fails): "ip" or "ip:port",
    /// IPv6 as "[addr]:port". Operator configuration: these are not subject to the SSRF guard. Empty = <see cref="DefaultDnsResolvers"/>.
    /// </summary>
    public string[]? DnsResolvers { get; set; }
}
