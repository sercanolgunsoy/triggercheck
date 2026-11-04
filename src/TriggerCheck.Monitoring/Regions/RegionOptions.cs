namespace TriggerCheck.Monitoring.Regions;

/// <summary>FEAT-6: multi-region checks (section <c>Regions</c> of the main app).</summary>
public sealed class RegionOptions
{
    public const string Section = "Regions";

    /// <summary>
    /// A region whose agent made no authenticated request for this long is stale: it is excluded from the quorum and
    /// flagged in the UI. Must be well above the agent's poll interval.
    /// </summary>
    public int AgentStaleAfterSeconds { get; set; } = 90;

    /// <summary>
    /// A region's last result votes while it is younger than this many check intervals (plus the probe timeout and
    /// <see cref="LeaseGraceSeconds"/>). Older results abstain.
    /// </summary>
    public double VoteWindowFactor { get; set; } = 2;

    /// <summary>A leased probe must be reported within its timeout plus this grace; later results are rejected as expired.</summary>
    public int LeaseGraceSeconds { get; set; } = 30;

    /// <summary>Tolerated clock difference of an agent: a reported probe time outside the lease (± this) is replaced by the server's time.</summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Agent API requests per region and minute (lease + results).</summary>
    public int AgentRequestsPerMinute { get; set; } = 300;
}
