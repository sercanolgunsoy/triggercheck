using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Regions;

/// <summary>One row of the per-region view of a monitor (central first).</summary>
/// <param name="Voting">The region's last result currently counts in the quorum.</param>
/// <param name="AvgMs24h">Average response time of its successful checks over the last 24 hours.</param>
public sealed record RegionRow(
    string Code, string Name, bool IsCentral, bool IsEnabled, bool IsStale, bool Voting,
    bool? LastIsUp, DateTime? LastCheckedAtUtc, int? LastResponseTimeMs, string? LastError,
    int Checks24h, int Failures24h, double? AvgMs24h);

/// <summary>FEAT-6: latest result and 24 h response time per region of one monitor (for the monitor details page).</summary>
public static class RegionBreakdown
{
    /// <summary>Rows for <paramref name="monitor"/>; empty when it has no remote region (single-region monitor).</summary>
    public static async Task<List<RegionRow>> LoadAsync(AppDbContext db, MonitorEntity monitor, DateTime nowUtc, RegionOptions o, CancellationToken ct)
    {
        var states = await db.MonitorRegionStates.AsNoTracking().Include(s => s.Region)
            .Where(s => s.MonitorId == monitor.Id).OrderBy(s => s.Region.Code).ToListAsync(ct);
        if (states.Count == 0) return [];

        var since = nowUtc.AddHours(-24);
        var stats = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == monitor.Id && c.CheckedAtUtc >= since && !c.IsConfirmation && !c.InMaintenance)
            .GroupBy(c => c.RegionId)
            .Select(g => new
            {
                RegionId = g.Key, Checks = g.Count(), Failures = g.Count(x => !x.IsUp),
                Avg = g.Where(x => x.IsUp).Average(x => (double?)x.ResponseTimeMs)
            })
            .ToListAsync(ct);

        var window = RegionQuorum.VoteWindow(monitor, o);
        bool Fresh(DateTime? at) => at is { } t && nowUtc - t <= window;
        var central = stats.FirstOrDefault(s => s.RegionId == null);
        var fingerprint = RegionQuorum.Fingerprint(monitor);
        var allowed = RegionQuorum.WithinPlan(states, await RegionQuorum.PlanMaxRegionsAsync(db, monitor.CustomerId, ct)).Select(s => s.RegionId).ToHashSet();
        var rows = new List<RegionRow>
        {
            new(RegionRules.CentralCode, "Central", true, true, false, Fresh(monitor.LastCheckedAtUtc),
                monitor.LastCheckedAtUtc is null ? null : monitor.LastError is null, monitor.LastCheckedAtUtc, monitor.LastResponseTimeMs, monitor.LastError,
                central?.Checks ?? 0, central?.Failures ?? 0, central?.Avg is double a ? Math.Round(a, 1) : null)
        };
        foreach (var s in states)
        {
            var st = stats.FirstOrDefault(x => x.RegionId == s.RegionId);
            var stale = RegionQuorum.IsAgentStale(s.Region, nowUtc, o);
            rows.Add(new RegionRow(s.Region.Code, s.Region.Name, false, s.Region.IsEnabled, stale,
                allowed.Contains(s.RegionId) && RegionQuorum.IsEligible(s, monitor, fingerprint, nowUtc, o),
                s.LastIsUp, s.LastCheckedAtUtc, s.LastResponseTimeMs, s.LastError,
                st?.Checks ?? 0, st?.Failures ?? 0, st?.Avg is double avg ? Math.Round(avg, 1) : null));
        }
        return rows;
    }
}
