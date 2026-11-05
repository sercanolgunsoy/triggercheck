using Microsoft.Extensions.Caching.Memory;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Web.Reporting;

/// <summary>
/// Built SLA reports per (customer, month, time zone), in memory: a closed month changes rarely (a monitor edit or
/// deletion) and is kept for <see cref="ClosedMonth"/>; the running month for <see cref="RunningMonth"/>. Keeps
/// repeated page views and PDF / CSV downloads from re-running the month's queries.
/// </summary>
public sealed class SlaReportCache(IMemoryCache cache, SlaReportService reports)
{
    public static readonly TimeSpan ClosedMonth = TimeSpan.FromHours(6);
    public static readonly TimeSpan RunningMonth = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ClosedWeek = TimeSpan.FromMinutes(30);

    public async Task<SlaReport?> GetAsync(int customerId, int year, int month, string? timeZoneId, CancellationToken ct)
    {
        var key = ("sla-report", customerId, year, month, timeZoneId ?? "");
        if (cache.TryGetValue(key, out SlaReport? hit)) return hit;
        var report = await reports.BuildAsync(customerId, year, month, timeZoneId, ct);
        if (report is not null)
            cache.Set(key, report, report.IsPartial ? RunningMonth : ClosedMonth);
        return report;
    }

    /// <summary>Weekly report (AI-6 panel view): a short cache, the running week as briefly as the running month.</summary>
    public async Task<SlaReport?> GetWeekAsync(int customerId, int isoYear, int week, string? timeZoneId, CancellationToken ct)
    {
        var key = ("sla-report-week", customerId, isoYear, week, timeZoneId ?? "");
        if (cache.TryGetValue(key, out SlaReport? hit)) return hit;
        var report = await reports.BuildWeekAsync(customerId, isoYear, week, timeZoneId, ct);
        if (report is not null)
            cache.Set(key, report, report.IsPartial ? RunningMonth : ClosedWeek);
        return report;
    }
}
