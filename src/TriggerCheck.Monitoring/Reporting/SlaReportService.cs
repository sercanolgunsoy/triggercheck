using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Monitoring.Reporting;

/// <summary>
/// Builds the monthly SLA report of one customer (FEAT-10). Month boundaries are midnight of the 1st in the report's
/// time zone (default UTC). Uptime and downtime are time based (see <see cref="SlaMath"/>): incident time minus
/// maintenance, over the part of the month the monitor existed. Response times come from the successful checks
/// (confirmation probes and checks taken during maintenance excluded).
/// </summary>
public class SlaReportService(AppDbContext db, TimeProvider time)
{
    public const string DefaultTimeZone = "UTC";

    /// <summary>UTC range of a calendar month in <paramref name="tz"/>: [start, end).</summary>
    public static (DateTime StartUtc, DateTime EndUtc) MonthRange(int year, int month, TimeZoneInfo tz)
    {
        var first = new DateTime(year, month, 1);
        return (MaintenanceSchedule.LocalToUtc(first, tz), MaintenanceSchedule.LocalToUtc(first.AddMonths(1), tz));
    }

    /// <summary>The zone for <paramref name="id"/>, falling back to UTC for unknown / empty ids.</summary>
    public static TimeZoneInfo ResolveZone(string? id) => MaintenanceSchedule.FindTimeZone(id) ?? TimeZoneInfo.Utc;

    /// <summary>UTC range of an ISO week (Monday 00:00 to the next Monday) in <paramref name="tz"/>: [start, end).</summary>
    public static (DateTime StartUtc, DateTime EndUtc) WeekRange(int isoYear, int week, TimeZoneInfo tz)
    {
        var monday = ISOWeek.ToDateTime(isoYear, week, DayOfWeek.Monday);
        return (MaintenanceSchedule.LocalToUtc(monday, tz), MaintenanceSchedule.LocalToUtc(monday.AddDays(7), tz));
    }

    /// <summary>ISO week key "yyyy-Www" of a local date.</summary>
    public static string WeekKey(DateTime localDate) =>
        string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(localDate):0000}-W{ISOWeek.GetWeekOfYear(localDate):00}");

    /// <summary>Parses "yyyy-Www" (weeks 1..53 that exist in that ISO year).</summary>
    public static bool TryParseWeekKey(string? key, out int isoYear, out int week)
    {
        isoYear = week = 0;
        if (key is not { Length: 8 } || key[4] != '-' || key[5] != 'W'
            || !int.TryParse(key.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out isoYear)
            || !int.TryParse(key.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out week)
            || isoYear < 2000 || week < 1 || week > ISOWeek.GetWeeksInYear(isoYear))
            return false;
        return true;
    }

    /// <summary>Null when the customer does not exist.</summary>
    public virtual Task<SlaReport?> BuildAsync(int customerId, int year, int month, string? timeZoneId, CancellationToken ct = default)
    {
        var (start, end) = MonthRange(year, month, ResolveZone(timeZoneId));
        return BuildRangeAsync(customerId, start, end, year, month, timeZoneId, null, ct);
    }

    /// <summary>Weekly variant (AI-6, panel only): the same figures over one ISO week. Null when the customer does not exist.</summary>
    public virtual Task<SlaReport?> BuildWeekAsync(int customerId, int isoYear, int week, string? timeZoneId, CancellationToken ct = default)
    {
        var (start, end) = WeekRange(isoYear, week, ResolveZone(timeZoneId));
        var monday = ISOWeek.ToDateTime(isoYear, week, DayOfWeek.Monday);
        return BuildRangeAsync(customerId, start, end, monday.Year, monday.Month, timeZoneId,
            string.Create(CultureInfo.InvariantCulture, $"{isoYear:0000}-W{week:00}"), ct);
    }

    private async Task<SlaReport?> BuildRangeAsync(int customerId, DateTime start, DateTime monthEnd, int year, int month, string? timeZoneId,
        string? weekKey, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId)
            .Select(c => new { c.Name, c.Plan.RetentionDays }).FirstOrDefaultAsync(ct);
        if (customer is null) return null;

        var tz = ResolveZone(timeZoneId);
        var zoneId = tz == TimeZoneInfo.Utc ? DefaultTimeZone : timeZoneId!.Trim();
        var now = time.GetUtcNow().UtcDateTime;
        var end = monthEnd < now ? monthEnd : now;
        var isPartial = monthEnd > now;

        var monitors = await db.Monitors.AsNoTracking()
            .Where(m => m.CustomerId == customerId && m.CreatedAtUtc < monthEnd)
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.Name, m.Type, m.SlaTarget, m.CreatedAtUtc })
            .ToListAsync(ct);
        var ids = monitors.Select(m => m.Id).ToList();

        var data = end > start ? await AvailabilityData.LoadAsync(db, customerId, ids, start, end, ct) : null;

        var checks = await db.CheckResults.AsNoTracking()
            .Where(c => ids.Contains(c.MonitorId) && c.CheckedAtUtc >= start && c.CheckedAtUtc < end && c.IsUp && !c.IsConfirmation && !c.InMaintenance)
            .GroupBy(c => c.MonitorId)
            .Select(g => new { MonitorId = g.Key, Count = g.Count(), Avg = g.Average(x => (double)x.ResponseTimeMs) })
            .ToDictionaryAsync(x => x.MonitorId, ct);

        var rows = new List<MonitorSlaReport>();
        TimeSpan totalCounted = TimeSpan.Zero, totalDown = TimeSpan.Zero;
        var resolvedDurations = new List<TimeSpan>();
        var incidentCount = 0;
        foreach (var m in monitors)
        {
            var a = data?.Compute(m.Id, m.CreatedAtUtc, start, end, now) ?? new Availability(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
            var started = (data?.Incidents(m.Id) ?? []).Where(i => i.StartedAtUtc >= start && i.StartedAtUtc < end).ToList();
            var durations = started.Where(i => i.ResolvedAtUtc is not null).Select(i => i.ResolvedAtUtc!.Value - i.StartedAtUtc).ToList();
            resolvedDurations.AddRange(durations);
            incidentCount += started.Count;
            totalCounted += a.Monitored - a.Maintenance;
            totalDown += a.Downtime;

            checks.TryGetValue(m.Id, out var c);
            int? p95 = c is null ? null : await P95Async([m.Id], start, end, c.Count, ct);
            var uptime = a.UptimePercent;
            rows.Add(new MonitorSlaReport(m.Id, m.Name, m.Type, m.SlaTarget, Round(uptime, 4),
                Round(a.Downtime.TotalMinutes, 1), Round(a.Maintenance.TotalMinutes, 1), Round(a.Monitored.TotalMinutes, 1),
                started.Count, durations.Count > 0 ? Round(durations.Average(d => d.TotalMinutes), 1) : null,
                c is null ? null : Round(c.Avg, 1), p95, c?.Count ?? 0,
                m.SlaTarget is { } target && uptime is { } u ? u >= target : null));
        }

        var totalChecks = checks.Values.Sum(c => c.Count);
        var summary = new SlaSummary(
            rows.Count,
            totalCounted > TimeSpan.Zero ? Round(Math.Clamp(100.0 * (1 - totalDown.TotalSeconds / totalCounted.TotalSeconds), 0, 100), 4) : null,
            Round(totalDown.TotalMinutes, 1),
            incidentCount,
            resolvedDurations.Count > 0 ? Round(resolvedDurations.Average(d => d.TotalMinutes), 1) : null,
            totalChecks > 0 ? Round(checks.Values.Sum(c => c.Avg * c.Count) / totalChecks, 1) : null,
            totalChecks > 0 ? await P95Async(ids, start, end, totalChecks, ct) : null,
            rows.Count(r => r.SlaMet == true),
            rows.Count(r => r.SlaMet == false));

        return new SlaReport(customerId, customer.Name, year, month, zoneId, start, end, now, isPartial,
            start < now.AddDays(-customer.RetentionDays), summary, rows, weekKey);
    }

    /// <summary>Nearest-rank 95th percentile of the successful checks' response times (one ordered query).</summary>
    private async Task<int?> P95Async(List<int> ids, DateTime start, DateTime end, int count, CancellationToken ct)
    {
        var index = SlaMath.PercentileIndex(count, 95);
        if (index < 0) return null;
        return await db.CheckResults.AsNoTracking()
            .Where(c => ids.Contains(c.MonitorId) && c.CheckedAtUtc >= start && c.CheckedAtUtc < end && c.IsUp && !c.IsConfirmation && !c.InMaintenance)
            .OrderBy(c => c.ResponseTimeMs).Skip(index).Select(c => (int?)c.ResponseTimeMs).FirstOrDefaultAsync(ct);
    }

    private static double Round(double v, int digits) => Math.Round(v, digits);
    private static double? Round(double? v, int digits) => v is { } x ? Math.Round(x, digits) : null;
}
