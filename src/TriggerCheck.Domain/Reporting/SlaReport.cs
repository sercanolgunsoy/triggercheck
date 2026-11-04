using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Domain.Reporting;

// Monthly SLA report (FEAT-10). Plain, serializable records with numbers computed from the database only, so a later
// narrative feature (AI-6) can consume them as input without re-computing anything. Monitor targets / hosts are never
// part of the report.

/// <param name="TimeZoneId">IANA zone the month boundaries were cut in.</param>
/// <param name="PeriodStartUtc">Start of the month in <paramref name="TimeZoneId"/>, as UTC (inclusive).</param>
/// <param name="PeriodEndUtc">End of the month (exclusive), or the generation time for the running month.</param>
/// <param name="IsPartial">The month is not over yet.</param>
/// <param name="HistoryIncomplete">Part of the month is older than the plan's data retention; numbers may be incomplete.</param>
public sealed record SlaReport(
    int CustomerId, string CustomerName, int Year, int Month, string TimeZoneId,
    DateTime PeriodStartUtc, DateTime PeriodEndUtc, DateTime GeneratedAtUtc, bool IsPartial, bool HistoryIncomplete,
    SlaSummary Summary, IReadOnlyList<MonitorSlaReport> Monitors)
{
    /// <summary>"yyyy-MM".</summary>
    public string MonthKey => $"{Year:0000}-{Month:00}";
}

/// <summary>Customer-wide totals. Uptime is time weighted over all monitors; response times over all successful checks.</summary>
public sealed record SlaSummary(
    int MonitorCount, double? UptimePercent, double DowntimeMinutes, int IncidentCount, double? MttrMinutes,
    double? AvgResponseMs, int? P95ResponseMs, int SlaMet, int SlaMissed);

/// <param name="UptimePercent">
/// 100 × (monitored − maintenance − downtime) / (monitored − maintenance); null when nothing was monitored outside maintenance.
/// </param>
/// <param name="MonitoredMinutes">Time in the period after the monitor was created.</param>
/// <param name="IncidentCount">Incidents that started in the period.</param>
/// <param name="MttrMinutes">Mean time to recovery of the resolved incidents that started in the period (full duration).</param>
/// <param name="AvgResponseMs">Average response time of the successful checks (maintenance and confirmation probes excluded).</param>
/// <param name="SlaMet">Null when no SLA target is set or the uptime is unknown.</param>
public sealed record MonitorSlaReport(
    int MonitorId, string Name, MonitorType Type, double? SlaTarget, double? UptimePercent,
    double DowntimeMinutes, double MaintenanceMinutes, double MonitoredMinutes, int IncidentCount, double? MttrMinutes,
    double? AvgResponseMs, int? P95ResponseMs, int CheckCount, bool? SlaMet);
