using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Api;

// Response shapes of the REST API. Times are UTC (ISO 8601 with "Z"). Request bodies for monitors use
// Areas/App/Support/MonitorInput, the same model as the panel form.

public sealed record MonitorDto(
    int Id, string Name, MonitorType Type, string Target, int? Port, string? Keyword, string HttpMethod, string ExpectedStatusCodes,
    int IntervalSeconds, int TimeoutSeconds, int FailureThreshold, MonitorStatus Status, bool IsPaused,
    DateTime? LastCheckedAtUtc, DateTime? NextCheckAtUtc, int? LastResponseTimeMs, string? LastError,
    DateTime? StatusChangedAtUtc, DateTime CreatedAtUtc)
{
    public static MonitorDto From(MonitorEntity m) => new(
        m.Id, m.Name, m.Type, m.Target, m.Port, m.Keyword, m.HttpMethod, m.ExpectedStatusCodes,
        m.IntervalSeconds, m.TimeoutSeconds, m.FailureThreshold, m.Status, m.Status == MonitorStatus.Paused || !m.IsEnabled,
        Utc.Of(m.LastCheckedAtUtc), Utc.Of(m.NextCheckAtUtc), m.LastResponseTimeMs, m.LastError,
        Utc.Of(m.StatusChangedAtUtc), Utc.Of(m.CreatedAtUtc));
}

public sealed record CheckDto(long Id, DateTime CheckedAtUtc, bool IsUp, int? StatusCode, int ResponseTimeMs, string? Error);

public sealed record IncidentDto(int Id, int MonitorId, string MonitorName, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, bool IsOpen, string? Cause);

public sealed record StatusCountsDto(int Total, int Up, int Down, int Paused, int Unknown);

public sealed record PlanDto(string Name, int MaxMonitors, int MinIntervalSeconds, int RetentionDays);

public sealed record StatusDto(string Customer, PlanDto Plan, StatusCountsDto Monitors, int OpenIncidents, double? Uptime24hPercent, DateTime GeneratedAtUtc);

internal static class Utc
{
    /// <summary>SQLite returns DateTime with Kind=Unspecified; the stored values are UTC.</summary>
    public static DateTime Of(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    public static DateTime? Of(DateTime? d) => d is DateTime x ? Of(x) : null;
}
