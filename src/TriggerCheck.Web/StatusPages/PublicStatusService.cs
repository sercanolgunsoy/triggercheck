using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Web.StatusPages;

// Public shape of a status page: what the HTML page, the JSON endpoint and the Atom feed show. It contains display
// names and times only: never monitor ids, targets, hosts, error texts or incident causes.

public static class PublicStatus
{
    public const string Operational = "operational";
    public const string PartialOutage = "partial_outage";
    public const string MajorOutage = "major_outage";
    public const string Maintenance = "maintenance";
    public const string Unknown = "unknown";
}

public sealed record PublicStatusPage(
    string Title, string? Announcement, string TimeZoneId, string Status, DateTime GeneratedAtUtc,
    IReadOnlyList<PublicMonitor> Monitors, IReadOnlyList<PublicMaintenance> Maintenance, IReadOnlyList<PublicIncident> Incidents);

/// <param name="Uptime90dPercent">Maintenance excluded; null without data.</param>
public sealed record PublicMonitor(string Name, string Status, double? Uptime90dPercent, IReadOnlyList<PublicDay> Days);

/// <param name="Date">Local date (page time zone), yyyy-MM-dd.</param>
/// <param name="UptimePercent">Null when the monitor did not exist yet or the day is outside the data retention.</param>
public sealed record PublicDay(string Date, double? UptimePercent, int DowntimeMinutes);

public sealed record PublicMaintenance(string Name, DateTime StartsAtUtc, DateTime EndsAtUtc, bool InProgress);

/// <param name="Monitor">Display name of the affected monitor.</param>
public sealed record PublicIncident(string Monitor, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, int DurationMinutes, IReadOnlyList<PublicNote> Notes);

public sealed record PublicNote(DateTime CreatedAtUtc, string Body);

/// <summary>A served status page with the ids the server needs (never serialized to visitors).</summary>
public sealed record StatusPageView(int PageId, int CustomerId, string Slug, StatusPageVisibility Visibility, PublicStatusPage Page);

/// <summary>Builds the public view of a status page (FEAT-4). Every query is filtered by the page's customer.</summary>
public sealed class PublicStatusService(AppDbContext db, TimeProvider time)
{
    public const int UptimeDays = 90;
    public const int IncidentHistoryDays = 30;
    public const int MaintenanceHorizonDays = 14;
    public const int MaxIncidents = 100;

    /// <summary>The page with this slug, or null when unknown, the customer is suspended or the page is over the plan's limit.</summary>
    public async Task<StatusPageView?> GetAsync(string? slug, CancellationToken ct = default)
    {
        var normalized = StatusPageRules.NormalizeSlug(slug);
        if (normalized.Length is 0 or > StatusPageRules.MaxSlugLength) return null;
        var page = await db.StatusPages.AsNoTracking().Include(p => p.Monitors)
            .Where(p => p.Slug == normalized && p.Customer.IsActive)
            .Select(p => new { Page = p, p.Customer.Plan.RetentionDays })
            .FirstOrDefaultAsync(ct);
        if (page is null || !await StatusPageRules.IsWithinPlanAsync(db, page.Page, ct)) return null;
        return await BuildAsync(page.Page, page.RetentionDays, ct);
    }

    private async Task<StatusPageView> BuildAsync(StatusPage page, int retentionDays, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cid = page.CustomerId;
        var tz = MaintenanceSchedule.FindTimeZone(page.TimeZoneId) ?? TimeZoneInfo.Utc;
        var rows = page.Monitors.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToList();
        var ids = rows.Select(r => r.MonitorId).ToList();
        var monitors = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == cid && ids.Contains(m.Id))
            .Select(m => new { m.Id, m.Status, m.IsEnabled, m.CreatedAtUtc })
            .ToDictionaryAsync(m => m.Id, ct);
        rows = rows.Where(r => monitors.ContainsKey(r.MonitorId)).ToList();
        var names = rows.ToDictionary(r => r.MonitorId, r => r.DisplayName);

        // 90 local days ending today.
        var today = DateOnly.FromDateTime(MaintenanceSchedule.UtcToLocal(now, tz));
        var firstDay = today.AddDays(-(UptimeDays - 1));
        var rangeStart = MaintenanceSchedule.LocalToUtc(firstDay.ToDateTime(TimeOnly.MinValue), tz);
        var historyFrom = now.AddDays(-IncidentHistoryDays);
        var loadFrom = rangeStart < historyFrom ? rangeStart : historyFrom;
        var data = await AvailabilityData.LoadAsync(db, cid, ids, loadFrom, now, ct);
        var retentionCutoff = now.AddDays(-retentionDays);
        var inMaintenance = await MaintenanceQueries.MonitorsInMaintenanceAsync(db, cid, ids, now, ct);

        var publicMonitors = new List<PublicMonitor>();
        foreach (var r in rows)
        {
            var m = monitors[r.MonitorId];
            // Incidents are purged with the plan's retention: days before it would look falsely perfect.
            var since = m.CreatedAtUtc > retentionCutoff ? m.CreatedAtUtc : retentionCutoff;
            var days = new List<PublicDay>(UptimeDays);
            for (var d = firstDay; d <= today; d = d.AddDays(1))
            {
                var from = MaintenanceSchedule.LocalToUtc(d.ToDateTime(TimeOnly.MinValue), tz);
                var to = MaintenanceSchedule.LocalToUtc(d.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
                if (to > now) to = now;
                var a = to > from ? data.Compute(r.MonitorId, since, from, to, now) : null;
                days.Add(new PublicDay(d.ToString("yyyy-MM-dd"), a?.UptimePercent is { } u ? Math.Round(u, 3) : null,
                    a is null ? 0 : (int)Math.Round(a.Downtime.TotalMinutes)));
            }
            var total = data.Compute(r.MonitorId, since, rangeStart, now, now).UptimePercent;
            var status = !m.IsEnabled || m.Status == MonitorStatus.Paused ? PublicStatus.Unknown
                : inMaintenance.Contains(r.MonitorId) ? PublicStatus.Maintenance
                : m.Status == MonitorStatus.Down ? PublicStatus.MajorOutage
                : m.Status == MonitorStatus.Up ? PublicStatus.Operational
                : PublicStatus.Unknown;
            publicMonitors.Add(new PublicMonitor(r.DisplayName, status, total is { } t ? Math.Round(t, 3) : null, days));
        }

        return new StatusPageView(page.Id, cid, page.Slug, page.Visibility, new PublicStatusPage(
            page.Title, page.Announcement, page.TimeZoneId, Overall(publicMonitors), now, publicMonitors,
            await MaintenanceAsync(cid, ids, now, ct), await IncidentsAsync(page.Id, cid, names, historyFrom, now, ct)));
    }

    public static string Overall(IReadOnlyList<PublicMonitor> monitors)
    {
        var down = monitors.Count(m => m.Status == PublicStatus.MajorOutage);
        if (down > 0) return down == monitors.Count ? PublicStatus.MajorOutage : PublicStatus.PartialOutage;
        if (monitors.Any(m => m.Status == PublicStatus.Maintenance)) return PublicStatus.Maintenance;
        if (monitors.Count == 0 || monitors.All(m => m.Status == PublicStatus.Unknown)) return PublicStatus.Unknown;
        return PublicStatus.Operational;
    }

    /// <summary>Active and upcoming (two weeks) maintenance of windows that cover at least one monitor of the page.</summary>
    private async Task<List<PublicMaintenance>> MaintenanceAsync(int cid, List<int> ids, DateTime now, CancellationToken ct)
    {
        var horizon = now.AddDays(MaintenanceHorizonDays);
        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == cid && (w.IsRecurring || (w.StartsAtUtc < horizon && w.EndsAtUtc > now)))
            .Where(w => w.AppliesToAllMonitors || w.Monitors.Any(m => m.CustomerId == cid && ids.Contains(m.Id)))
            .ToListAsync(ct);
        if (ids.Count == 0) return [];
        return windows
            .Select(w => (w.Name, Occurrence: MaintenanceSchedule.CurrentOrNext(w, now)))
            .Where(x => x.Occurrence is { } o && o.StartUtc < horizon)
            .Select(x => new PublicMaintenance(x.Name, x.Occurrence!.Value.StartUtc, x.Occurrence.Value.EndUtc, x.Occurrence.Value.Contains(now)))
            .OrderBy(x => x.StartsAtUtc).ThenBy(x => x.Name)
            .ToList();
    }

    private async Task<List<PublicIncident>> IncidentsAsync(int pageId, int cid, Dictionary<int, string> names, DateTime from, DateTime now, CancellationToken ct)
    {
        var ids = names.Keys.ToList();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.MonitorId) && i.Monitor.CustomerId == cid && (i.StartedAtUtc >= from || i.ResolvedAtUtc == null))
            .OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).Take(MaxIncidents)
            .Select(i => new { i.Id, i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc })
            .ToListAsync(ct);
        var incidentIds = incidents.Select(i => i.Id).ToList();
        var notes = (await db.StatusPageNotes.AsNoTracking()
                .Where(n => n.StatusPageId == pageId && incidentIds.Contains(n.IncidentId))
                .Select(n => new { n.IncidentId, n.CreatedAtUtc, n.Body }).ToListAsync(ct))
            .GroupBy(n => n.IncidentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.CreatedAtUtc).Select(n => new PublicNote(Utc(n.CreatedAtUtc), n.Body)).ToList());
        return incidents.Select(i => new PublicIncident(names[i.MonitorId], Utc(i.StartedAtUtc), i.ResolvedAtUtc is { } r ? Utc(r) : null,
                (int)Math.Max(0, Math.Round(((i.ResolvedAtUtc ?? now) - i.StartedAtUtc).TotalMinutes)),
                notes.TryGetValue(i.Id, out var n) ? n : []))
            .ToList();
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
