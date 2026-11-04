using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Monitoring.Reporting;

/// <summary>One incident as needed for availability math.</summary>
public sealed record IncidentSpan(int Id, int MonitorId, DateTime StartedAtUtc, DateTime? ResolvedAtUtc)
{
    /// <summary>The incident's range; open incidents end at <paramref name="nowUtc"/>.</summary>
    public TimeRange Range(DateTime nowUtc) => new(StartedAtUtc, ResolvedAtUtc ?? (nowUtc > StartedAtUtc ? nowUtc : StartedAtUtc));
}

/// <summary>
/// Incidents and maintenance periods of some monitors of ONE customer over a range, loaded with two queries. Every query
/// is filtered by the customer id (tenant isolation): monitor ids of other customers simply yield nothing.
/// </summary>
public sealed class AvailabilityData
{
    private readonly Dictionary<int, List<IncidentSpan>> _incidents;
    private readonly Dictionary<int, List<TimeRange>> _maintenance;

    private AvailabilityData(Dictionary<int, List<IncidentSpan>> incidents, Dictionary<int, List<TimeRange>> maintenance)
    {
        _incidents = incidents;
        _maintenance = maintenance;
    }

    public IReadOnlyList<IncidentSpan> Incidents(int monitorId) => _incidents.TryGetValue(monitorId, out var l) ? l : [];
    public IReadOnlyList<TimeRange> Maintenance(int monitorId) => _maintenance.TryGetValue(monitorId, out var l) ? l : [];

    public static async Task<AvailabilityData> LoadAsync(AppDbContext db, int customerId, IReadOnlyCollection<int> monitorIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var ids = monitorIds.Distinct().ToList();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.MonitorId) && i.Monitor.CustomerId == customerId
                        && i.StartedAtUtc < toUtc && (i.ResolvedAtUtc == null || i.ResolvedAtUtc > fromUtc))
            .Select(i => new IncidentSpan(i.Id, i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc))
            .ToListAsync(ct);

        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == customerId && (w.IsRecurring || (w.StartsAtUtc < toUtc && w.EndsAtUtc > fromUtc)))
            .Select(w => new { Window = w, Ids = w.Monitors.Where(m => m.CustomerId == customerId).Select(m => m.Id).ToList() })
            .ToListAsync(ct);

        var maintenance = new Dictionary<int, List<TimeRange>>();
        foreach (var w in windows)
        {
            var occurrences = MaintenanceSchedule.Occurrences(w.Window, fromUtc, toUtc).Select(o => new TimeRange(o.StartUtc, o.EndUtc)).ToList();
            if (occurrences.Count == 0) continue;
            foreach (var id in w.Window.AppliesToAllMonitors ? ids : w.Ids.Where(ids.Contains))
            {
                if (!maintenance.TryGetValue(id, out var list)) maintenance[id] = list = [];
                list.AddRange(occurrences);
            }
        }
        return new AvailabilityData(
            incidents.GroupBy(i => i.MonitorId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.StartedAtUtc).ToList()),
            maintenance.ToDictionary(kv => kv.Key, kv => SlaMath.Merge(kv.Value)));
    }

    /// <summary>Availability of a monitor over [fromUtc, toUtc) (toUtc already capped at "now" by the caller).</summary>
    public Availability Compute(int monitorId, DateTime createdUtc, DateTime fromUtc, DateTime toUtc, DateTime nowUtc) =>
        SlaMath.Compute(fromUtc, toUtc, createdUtc, Incidents(monitorId).Select(i => i.Range(nowUtc)), Maintenance(monitorId));
}
