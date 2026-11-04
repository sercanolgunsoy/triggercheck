namespace TriggerCheck.Domain.Entities;

/// <summary>
/// FEAT-6: a remote probe region served by a probe agent (see src/TriggerCheck.ProbeAgent). The central scheduler is
/// the implicit region "central" and has no row. Agents authenticate with <c>tcr_&lt;Prefix&gt;_&lt;secret&gt;</c>; only the
/// SHA-256 hash of the credential is stored and the credential is shown once (create / rotate).
/// </summary>
public class Region
{
    public int Id { get; set; }
    /// <summary>Stable short identifier, e.g. <c>eu-central</c> (lower-case letters, digits and dashes). The agent sends it.</summary>
    public string Code { get; set; } = "";
    /// <summary>Display name, e.g. "Frankfurt".</summary>
    public string Name { get; set; } = "";
    /// <summary>Disabled regions get no leases, their agent is rejected and they never vote.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Public, unique part of the agent credential.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Lower-case hex SHA-256 of the full agent credential.</summary>
    public string SecretHash { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CredentialRotatedAtUtc { get; set; }
    /// <summary>Last authenticated agent request (written at most every few seconds). Old = agent stale.</summary>
    public DateTime? LastSeenAtUtc { get; set; }
}

/// <summary>
/// FEAT-6: assignment of a monitor to a remote region and that region's probe state for the monitor. A monitor without
/// rows is checked by the central scheduler only (the default). Central's own state stays on <see cref="Monitor"/>.
/// </summary>
public class MonitorRegionState
{
    public int MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public int RegionId { get; set; }
    public Region Region { get; set; } = null!;

    /// <summary>Due time of the next probe in this region; null = due now.</summary>
    public DateTime? NextCheckAtUtc { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }
    /// <summary>Outcome of the last accepted result; null = no result yet.</summary>
    public bool? LastIsUp { get; set; }
    public string? LastError { get; set; }
    public int? LastResponseTimeMs { get; set; }
    /// <summary>Consecutive failed results in this region.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>Probe time of the first failure of the current streak; null while up.</summary>
    public DateTime? FailingSinceUtc { get; set; }
    /// <summary>The last result was taken in a maintenance window (it does not vote).</summary>
    public bool LastInMaintenance { get; set; }

    // ---- current lease (one outstanding probe per monitor and region) ----
    /// <summary>Random id of the leased probe; results must quote it. Null = not leased.</summary>
    public string? LeaseItemId { get; set; }
    public DateTime? LeasedAtUtc { get; set; }
    public DateTime? LeasedUntilUtc { get; set; }
    /// <summary>Fingerprint of the probe settings sent with the lease; a result for different settings is discarded.</summary>
    public string? LeaseFingerprint { get; set; }
    /// <summary>Item id of the last accepted result, so a replay is recognized as a duplicate.</summary>
    public string? LastResultItemId { get; set; }
    /// <summary>Probe settings fingerprint of the last result: after an edit of the monitor the old result no longer votes.</summary>
    public string? LastResultFingerprint { get; set; }
}
