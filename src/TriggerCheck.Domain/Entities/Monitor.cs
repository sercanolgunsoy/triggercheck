namespace TriggerCheck.Domain.Entities;

public class Monitor
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string Name { get; set; } = "";
    public MonitorType Type { get; set; } = MonitorType.Http;

    /// <summary>
    /// For Http/Keyword/JsonApi: absolute URL. For Tcp/Ping: host name or IP. For Dns: the host name to query.
    /// For Heartbeat: a display string ("/hb/{prefix}…"), never the token.
    /// </summary>
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

    // ---- Dns / JsonApi assertion ----
    /// <summary>Dns only: record type to query.</summary>
    public DnsRecordType? DnsRecordType { get; set; }
    /// <summary>JsonApi only: path like <c>$.data.items[0].status</c> (dot / index / quoted-bracket segments only).</summary>
    public string? JsonPath { get; set; }
    /// <summary>JsonApi: how the value at <see cref="JsonPath"/> is compared. Dns: Equal / Contains for <see cref="ExpectedValue"/>.</summary>
    public AssertionOperator? AssertionOperator { get; set; }
    /// <summary>JsonApi / Dns: expected value. Dns: optional (null = at least one record must exist).</summary>
    public string? ExpectedValue { get; set; }

    // ---- Heartbeat (push) ----
    /// <summary>Heartbeat: how long after the expected ping (LastHeartbeatAtUtc + interval) the monitor is still UP.</summary>
    public int GracePeriodSeconds { get; set; }
    /// <summary>Heartbeat: SHA-256 (hex) of the secret ping token. The token itself is never stored.</summary>
    public string? HeartbeatTokenHash { get; set; }
    /// <summary>Heartbeat: first characters of the token, shown to identify it.</summary>
    public string? HeartbeatTokenPrefix { get; set; }
    /// <summary>Heartbeat: time of the last ping received.</summary>
    public DateTime? LastHeartbeatAtUtc { get; set; }
    /// <summary>Heartbeat: the last ping reported a failure (<c>?status=fail</c>).</summary>
    public bool LastHeartbeatFailed { get; set; }
    /// <summary>Heartbeat: optional short message of the last ping (truncated, secrets masked).</summary>
    public string? LastHeartbeatMessage { get; set; }

    /// <summary>Https Http/Keyword monitors: send a one-time alert this many days before the certificate expires. 0 = off.</summary>
    public int CertWarnDays { get; set; } = 14;

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

    // ---- TLS certificate of https targets, captured on each new connection by the probe ----
    /// <summary>NotAfter of the server certificate last seen (UTC).</summary>
    public DateTime? CertExpiresAtUtc { get; set; }
    public string? CertSubject { get; set; }
    public string? CertIssuer { get; set; }
    /// <summary>Validation problem of the last seen certificate (expired, name mismatch, untrusted chain...); null when valid.</summary>
    public string? CertError { get; set; }
    public DateTime? CertCheckedAtUtc { get; set; }
    /// <summary>
    /// <see cref="CertExpiresAtUtc"/> of the certificate an "expiring" alert was already sent for. A renewed certificate
    /// has a different NotAfter, so the next threshold crossing alerts again.
    /// </summary>
    public DateTime? CertWarningSentForUtc { get; set; }
    /// <summary>Warning level (days before expiry) of the last "expiring" alert for that certificate.</summary>
    public int? CertWarningSentDays { get; set; }

    // ---- confirm before alert (see MonitorCheckRunner) ----
    /// <summary>Time of a failed probe that is waiting for its confirmation probe; null when none is pending.</summary>
    public DateTime? PendingConfirmationSinceUtc { get; set; }
    /// <summary><see cref="Version"/> when the confirmation was scheduled; a different Version means the monitor was changed and the confirmation is void.</summary>
    public int? PendingConfirmationVersion { get; set; }

    // ---- FEAT-6: multi-region checks (see RegionQuorum) ----
    /// <summary>Probe settings fingerprint of the last central probe; a probe of older settings does not vote.</summary>
    public string? LastProbeFingerprint { get; set; }
    /// <summary>When a failure last pulled the regions' next probes forward (at most once per interval).</summary>
    public DateTime? RegionsExpeditedAtUtc { get; set; }

    /// <summary>
    /// Application-managed optimistic concurrency token (SQLite has no rowversion). Bumped by user changes (edit,
    /// pause, resume, SuperAdmin interval clamp / reactivation) and by the check runner whenever it changes Status or
    /// opens/closes an incident. Writers that lose the race get a concurrency exception and retry on fresh state.
    /// </summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CheckResult> CheckResults { get; set; } = new List<CheckResult>();
    public ICollection<Incident> Incidents { get; set; } = new List<Incident>();
}
