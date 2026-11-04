using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages;

[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db) : PageModel
{
    public int CustomersTotal { get; private set; }
    public int CustomersActive { get; private set; }
    public int CustomersSuspended => CustomersTotal - CustomersActive;
    public Dictionary<MonitorStatus, int> MonitorsByStatus { get; private set; } = new();
    public int MonitorsTotal => MonitorsByStatus.Values.Sum();
    public List<PlanRow> MonitorsPerPlan { get; private set; } = [];
    public int ChecksLastHour { get; private set; }
    public int ChecksLast24h { get; private set; }
    public int OpenIncidentCount { get; private set; }
    public List<IncidentRow> OpenIncidents { get; private set; } = [];
    public decimal EstimatedMrr { get; private set; }
    public int EnabledMonitors { get; private set; }
    public int OverdueMonitors { get; private set; }
    public DateTime? OldestOverdueNextCheckUtc { get; private set; }

    public record PlanRow(int PlanId, string PlanName, bool PlanActive, decimal Price, int Customers, int ActiveCustomers, int Monitors);
    public record IncidentRow(int IncidentId, DateTime StartedAtUtc, string? Cause, int MonitorId, string MonitorName, string Target, int CustomerId, string CustomerName);

    public async Task OnGetAsync()
    {
        var now = DateTime.UtcNow;

        CustomersTotal = await db.Customers.CountAsync();
        CustomersActive = await db.Customers.CountAsync(c => c.IsActive);

        var statusCounts = await db.Monitors.GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        MonitorsByStatus = Enum.GetValues<MonitorStatus>().ToDictionary(s => s, s => statusCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0);

        var plans = await db.Plans.AsNoTracking().OrderByDescending(p => p.MinIntervalSeconds).ThenBy(p => p.Name).ToListAsync();
        var custByPlan = await db.Customers.GroupBy(c => c.PlanId)
            .Select(g => new { PlanId = g.Key, Total = g.Count(), Active = g.Count(c => c.IsActive) }).ToListAsync();
        var monByPlan = await db.Monitors.GroupBy(m => m.Customer.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() }).ToListAsync();
        MonitorsPerPlan = plans.Select(p => new PlanRow(p.Id, p.Name, p.IsActive, p.MonthlyPrice,
            custByPlan.FirstOrDefault(x => x.PlanId == p.Id)?.Total ?? 0,
            custByPlan.FirstOrDefault(x => x.PlanId == p.Id)?.Active ?? 0,
            monByPlan.FirstOrDefault(x => x.PlanId == p.Id)?.Count ?? 0)).ToList();
        EstimatedMrr = MonitorsPerPlan.Sum(r => r.Price * r.ActiveCustomers);

        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        ChecksLastHour = await db.CheckResults.CountAsync(c => c.CheckedAtUtc >= hourAgo);
        ChecksLast24h = await db.CheckResults.CountAsync(c => c.CheckedAtUtc >= dayAgo);

        OpenIncidentCount = await db.Incidents.CountAsync(i => i.ResolvedAtUtc == null);
        OpenIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAtUtc == null)
            .OrderByDescending(i => i.StartedAtUtc)
            .Take(20)
            .Select(i => new IncidentRow(i.Id, i.StartedAtUtc, i.Cause, i.MonitorId, i.Monitor.Name, i.Monitor.Target, i.Monitor.CustomerId, i.Monitor.Customer.Name))
            .ToListAsync();

        // Scheduler health: enabled monitors of active customers whose next check is more than 2x interval in the past.
        // The fastest interval is 15s, so anything not at least 30s overdue can be excluded in SQL.
        EnabledMonitors = await db.Monitors.CountAsync(m => m.IsEnabled && m.Customer.IsActive);
        var threshold = now.AddSeconds(-2 * PlanRulesMinInterval);
        var candidates = await db.Monitors.AsNoTracking()
            .Where(m => m.IsEnabled && m.Customer.IsActive && m.NextCheckAtUtc != null && m.NextCheckAtUtc < threshold)
            .Select(m => new { m.NextCheckAtUtc, m.IntervalSeconds })
            .ToListAsync();
        var overdue = candidates.Where(m => m.NextCheckAtUtc!.Value < now.AddSeconds(-2 * m.IntervalSeconds)).ToList();
        OverdueMonitors = overdue.Count;
        OldestOverdueNextCheckUtc = overdue.Count > 0 ? overdue.Min(m => m.NextCheckAtUtc) : null;
    }

    private static int PlanRulesMinInterval => Domain.PlanRules.SupportedIntervals.Min();
}
