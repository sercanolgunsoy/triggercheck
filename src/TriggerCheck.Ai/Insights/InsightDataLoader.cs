using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;

namespace TriggerCheck.Ai.Insights;

/// <summary>
/// Loads the platform-wide data the AI-12 rules need (SuperAdmin only, read-only): active customers with their plan
/// limits, their monitors (with region counts), incidents of the window and acknowledgements of the abandoned period,
/// alert counts, status pages, the busiest SMS / voice day and the AI quota usage (AI-10). Aggregations run in SQL where
/// the provider can; incidents of the window are read as narrow rows (start, end, acknowledgement).
/// </summary>
public sealed class InsightDataLoader(AppDbContext db, IAiUsage usage, IOptions<AlertingOptions> alerting, TimeProvider time)
{
    public async Task<InsightInput> LoadAsync(AiInsightOptions options, CancellationToken ct = default)
    {
        var t = InsightRules.Thresholds(options);
        var now = time.GetUtcNow().UtcDateTime;
        var windowStart = now.AddDays(-t.WindowDays);
        var ackStart = now.AddDays(-t.AbandonedDownDays);

        var customers = await db.Customers.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.Id)
            .Select(c => new InsightCustomer(c.Id, c.Name, c.Plan.Name, c.Plan.MaxMonitors, c.Plan.MinIntervalSeconds, c.Plan.MaxStatusPages,
                c.Plan.MaxRegions, c.Plan.AllowsSmsVoice))
            .ToListAsync(ct);
        var ids = customers.Select(c => c.Id).ToList();

        var regionCounts = await db.MonitorRegionStates.AsNoTracking()
            .Where(s => ids.Contains(s.Monitor.CustomerId))
            .GroupBy(s => s.MonitorId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var monitors = (await db.Monitors.AsNoTracking().Where(m => ids.Contains(m.CustomerId))
                .Select(m => new { m.Id, m.CustomerId, m.Name, m.Type, m.IsEnabled, m.Status, m.StatusChangedAtUtc, m.IntervalSeconds })
                .ToListAsync(ct))
            .Select(m => new InsightMonitor(m.Id, m.CustomerId, m.Name, m.Type, m.IsEnabled, m.Status, m.StatusChangedAtUtc, m.IntervalSeconds,
                1 + regionCounts.GetValueOrDefault(m.Id)))
            .ToList();

        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.Monitor.CustomerId) && (i.StartedAtUtc >= windowStart || (i.AcknowledgedAtUtc != null && i.AcknowledgedAtUtc >= ackStart)))
            .Select(i => new InsightIncident(i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc, i.AcknowledgedAtUtc))
            .ToListAsync(ct);

        // Notifications reference the monitor without a navigation property.
        var alerts = await db.Notifications.AsNoTracking()
            .Where(n => n.SentAtUtc >= windowStart && db.Monitors.Any(m => m.Id == n.MonitorId && ids.Contains(m.CustomerId)))
            .GroupBy(n => n.MonitorId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var pages = await db.StatusPages.AsNoTracking().Where(p => ids.Contains(p.CustomerId))
            .GroupBy(p => p.CustomerId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var smsDay = windowStart.Date;
        var sms = await db.SmsVoiceUsage.AsNoTracking().Where(u => u.DayUtc >= smsDay && ids.Contains(u.CustomerId))
            .GroupBy(u => u.CustomerId).Select(g => new { g.Key, Peak = g.Max(u => u.Count) })
            .ToDictionaryAsync(x => x.Key, x => x.Peak, ct);

        var ai = (await usage.GetAllMonthlyUsageAsync(ct))
            .ToDictionary(s => s.CustomerId, s => new InsightAiUsage(s.Calls, s.CallLimit, s.PercentUsed));

        return new InsightInput(customers, monitors, incidents, alerts, pages, sms, Math.Max(0, alerting.Value.SmsVoiceDailyLimitPerCustomer), ai);
    }
}

/// <summary>Loads the data and runs <see cref="InsightRules"/> (works with AI switched off).</summary>
public sealed class PlatformInsights(InsightDataLoader loader, IOptions<AiOptions> options, TimeProvider time)
{
    public AiInsightOptions Options => options.Value.Insights;

    public async Task<InsightReport> ComputeAsync(CancellationToken ct = default)
    {
        var input = await loader.LoadAsync(Options, ct);
        return InsightRules.Compute(input, Options, time.GetUtcNow().UtcDateTime);
    }
}
