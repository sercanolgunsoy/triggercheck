using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Maintenance;

/// <summary>Which monitors of a customer are in maintenance (FEAT-3). Every query is scoped to one customer.</summary>
public static class MaintenanceQueries
{
    /// <summary>Windows of the customer that apply to <paramref name="monitorId"/> and may overlap [fromUtc, toUtc).</summary>
    private static IQueryable<MaintenanceWindow> For(AppDbContext db, int customerId, int monitorId, DateTime fromUtc, DateTime toUtc) =>
        db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == customerId && (w.AppliesToAllMonitors || w.Monitors.Any(m => m.Id == monitorId)))
            .Where(w => w.IsRecurring || (w.StartsAtUtc < toUtc && w.EndsAtUtc > fromUtc));

    /// <summary>True when a maintenance window of the monitor's customer covers the monitor at <paramref name="atUtc"/>.</summary>
    public static async Task<bool> IsInMaintenanceAsync(AppDbContext db, int customerId, int monitorId, DateTime atUtc, CancellationToken ct = default)
    {
        var windows = await For(db, customerId, monitorId, atUtc, atUtc.AddTicks(1)).ToListAsync(ct);
        return windows.Any(w => MaintenanceSchedule.ActiveAt(w, atUtc) is not null);
    }

    /// <summary>Maintenance periods of one monitor overlapping [fromUtc, toUtc), merged and clipped to the range (chart bands).</summary>
    public static async Task<List<MaintenanceOccurrence>> OccurrencesAsync(AppDbContext db, int customerId, int monitorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var windows = await For(db, customerId, monitorId, fromUtc, toUtc).ToListAsync(ct);
        var all = windows.SelectMany(w => MaintenanceSchedule.Occurrences(w, fromUtc, toUtc))
            .Select(o => new MaintenanceOccurrence(o.StartUtc < fromUtc ? fromUtc : o.StartUtc, o.EndUtc > toUtc ? toUtc : o.EndUtc))
            .OrderBy(o => o.StartUtc);
        var merged = new List<MaintenanceOccurrence>();
        foreach (var o in all)
        {
            if (merged.Count > 0 && o.StartUtc <= merged[^1].EndUtc)
                merged[^1] = merged[^1] with { EndUtc = o.EndUtc > merged[^1].EndUtc ? o.EndUtc : merged[^1].EndUtc };
            else
                merged.Add(o);
        }
        return merged;
    }

    /// <summary>Ids of the customer's monitors (among <paramref name="monitorIds"/>) that are in maintenance at <paramref name="atUtc"/>.</summary>
    public static async Task<HashSet<int>> MonitorsInMaintenanceAsync(AppDbContext db, int customerId, IReadOnlyCollection<int> monitorIds, DateTime atUtc, CancellationToken ct = default)
    {
        var result = new HashSet<int>();
        if (monitorIds.Count == 0) return result;
        var end = atUtc.AddTicks(1);
        var active = (await db.MaintenanceWindows.AsNoTracking()
                .Where(w => w.CustomerId == customerId && (w.IsRecurring || (w.StartsAtUtc < end && w.EndsAtUtc > atUtc)))
                .ToListAsync(ct))
            .Where(w => MaintenanceSchedule.ActiveAt(w, atUtc) is not null)
            .ToList();
        if (active.Count == 0) return result;
        if (active.Any(w => w.AppliesToAllMonitors))
        {
            result.UnionWith(monitorIds);
            return result;
        }
        var ids = active.Select(w => w.Id).ToList();
        var covered = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .SelectMany(w => w.Monitors.Where(m => m.CustomerId == customerId).Select(m => m.Id))
            .ToListAsync(ct);
        result.UnionWith(covered.Where(monitorIds.Contains));
        return result;
    }
}
