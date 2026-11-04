using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Api;

// Response shapes of the REST API. Times are UTC (ISO 8601 with "Z"). Request bodies for monitors use
// Areas/App/Support/MonitorInput, the same model as the panel form.

public sealed record MonitorDto(
    int Id, string Name, MonitorType Type, string Target, int? Port, string? Keyword, string HttpMethod, string ExpectedStatusCodes,
    int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, MonitorStatus Status, bool IsPaused,
    DateTime? LastCheckedAtUtc, DateTime? NextCheckAtUtc, int? LastResponseTimeMs, string? LastError,
    DateTime? StatusChangedAtUtc, DateTime CreatedAtUtc, bool InMaintenance,
    DnsRecordType? DnsRecordType, string? JsonPath, AssertionOperator? AssertionOperator, string? ExpectedValue,
    int? GracePeriodSeconds, string? HeartbeatTokenPrefix, DateTime? LastHeartbeatAtUtc, bool? LastHeartbeatFailed, string? LastHeartbeatMessage)
{
    /// <summary>
    /// Heartbeat only, and only in the response that issued the token (create as / change to Heartbeat, regeneration):
    /// the full ping URL. It is never stored or returned again (only <see cref="HeartbeatTokenPrefix"/>).
    /// </summary>
    public string? PingUrl { get; init; }

    /// <param name="inMaintenance">The monitor is in an active maintenance window (FEAT-3).</param>
    public static MonitorDto From(MonitorEntity m, bool inMaintenance = false)
    {
        var hb = m.Type == MonitorType.Heartbeat;
        return new(
            m.Id, m.Name, m.Type, m.Target, m.Port, m.Keyword, m.HttpMethod, m.ExpectedStatusCodes,
            m.IntervalSeconds, m.TimeoutSeconds, m.FailureThreshold, m.Status, m.Status == MonitorStatus.Paused || !m.IsEnabled,
            Utc.Of(m.LastCheckedAtUtc), Utc.Of(m.NextCheckAtUtc), m.LastResponseTimeMs, m.LastError,
            Utc.Of(m.StatusChangedAtUtc), Utc.Of(m.CreatedAtUtc), inMaintenance,
            m.DnsRecordType, m.JsonPath, m.AssertionOperator, m.ExpectedValue,
            hb ? m.GracePeriodSeconds : null, hb ? m.HeartbeatTokenPrefix : null, hb ? Utc.Of(m.LastHeartbeatAtUtc) : null,
            hb ? m.LastHeartbeatFailed : null, hb ? m.LastHeartbeatMessage : null);
    }
}

/// <param name="InMaintenance">Taken during a maintenance window; not counted in uptime (FEAT-3).</param>
/// <param name="RegionCode">FEAT-6: region that took the probe ("central" = the main app).</param>
public sealed record CheckDto(long Id, DateTime CheckedAtUtc, bool IsUp, int? StatusCode, int ResponseTimeMs, string? Error, bool InMaintenance = false,
    string RegionCode = TriggerCheck.Domain.RegionRules.CentralCode);

/// <param name="AcknowledgedAtUtc">When the incident was acknowledged (FEAT-5), null if not.</param>
/// <param name="EscalationLevel">Escalation level last sent (0 = only the DOWN alert).</param>
/// <param name="ResolvedDuringMaintenance">The monitor recovered during a maintenance window (FEAT-3).</param>
public sealed record IncidentDto(int Id, int MonitorId, string MonitorName, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, bool IsOpen, string? Cause,
    DateTime? AcknowledgedAtUtc = null, string? AcknowledgeNote = null, int EscalationLevel = 0, bool ResolvedDuringMaintenance = false);

/// <summary>Body of POST /incidents/{id}/acknowledge (optional).</summary>
public sealed record AcknowledgeRequest(string? Note);

public sealed record StatusCountsDto(int Total, int Up, int Down, int Paused, int Unknown);

public sealed record PlanDto(string Name, int MaxMonitors, int MinIntervalSeconds, int RetentionDays);

public sealed record StatusDto(string Customer, PlanDto Plan, StatusCountsDto Monitors, int OpenIncidents, double? Uptime24hPercent, DateTime GeneratedAtUtc);

internal static class Utc
{
    /// <summary>SQLite returns DateTime with Kind=Unspecified; the stored values are UTC.</summary>
    public static DateTime Of(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    public static DateTime? Of(DateTime? d) => d is DateTime x ? Of(x) : null;
}
