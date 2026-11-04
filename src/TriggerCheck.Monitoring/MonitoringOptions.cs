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
    /// Confirm before alert: when a failed probe would take a monitor DOWN, run one more probe after
    /// <see cref="ConfirmationDelaySeconds"/> before opening an incident. See MonitorCheckRunner.
    /// </summary>
    public bool ConfirmBeforeDown { get; set; } = true;
    /// <summary>Delay before the confirmation probe (0-60 s).</summary>
    public int ConfirmationDelaySeconds { get; set; } = 5;
}
