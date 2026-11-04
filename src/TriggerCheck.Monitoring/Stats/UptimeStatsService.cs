using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Stats;

public record UptimeSummary(int MonitorId, int TotalChecks, int UpChecks, double? UptimePercent, double? AvgResponseMs);

public interface IUptimeStatsService
{
    /// <summary>
    /// Uptime % and average response time per monitor for [fromUtc, toUtc]. Monitors without checks get null values.
    /// Confirmation probes and checks taken during maintenance windows (FEAT-3) are not counted.
    /// Multi-region monitors (FEAT-6): uptime follows the central check timeline, each check counting as up unless the
    /// voted monitor status after it was DOWN (<see cref="Domain.Entities.CheckResult.VotedUp"/>); remote regions' own
    /// results are not counted here (they are shown per region on the monitor page). Single-region monitors: unchanged.
    /// </summary>
    Task<Dictionary<int, UptimeSummary>> GetSummariesAsync(IEnumerable<int> monitorIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

public class UptimeStatsService(AppDbContext db) : IUptimeStatsService
{
    public async Task<Dictionary<int, UptimeSummary>> GetSummariesAsync(IEnumerable<int> monitorIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var ids = monitorIds.Distinct().ToList();
        var rows = await db.CheckResults.AsNoTracking()
            .Where(c => ids.Contains(c.MonitorId) && c.CheckedAtUtc >= fromUtc && c.CheckedAtUtc <= toUtc && !c.IsConfirmation && !c.InMaintenance
                        && c.RegionId == null)
            .GroupBy(c => c.MonitorId)
            .Select(g => new { MonitorId = g.Key, Total = g.Count(), Up = g.Count(x => x.VotedUp ?? x.IsUp), Avg = g.Average(x => (double)x.ResponseTimeMs) })
            .ToListAsync(ct);

        var result = ids.ToDictionary(id => id, id => new UptimeSummary(id, 0, 0, null, null));
        foreach (var r in rows)
            result[r.MonitorId] = new UptimeSummary(r.MonitorId, r.Total, r.Up, Math.Round(100.0 * r.Up / r.Total, 3), Math.Round(r.Avg, 1));
        return result;
    }
}
