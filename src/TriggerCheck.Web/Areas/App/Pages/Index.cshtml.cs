using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages;

public class IndexModel(AppDbContext db, ICurrentCustomer current, IUptimeStatsService stats, PlanUsageService usage) : PageModel
{
    public record Row(int Id, string Name, MonitorType Type, string Target, MonitorStatus Status, int? LastResponseTimeMs, DateTime? LastCheckedAtUtc, double? Uptime24h);
    public record OpenIncidentRow(int Id, int MonitorId, string MonitorName, DateTime StartedAtUtc, string? Cause);

    public string CustomerName { get; private set; } = "";
    public List<Row> Monitors { get; private set; } = [];
    public List<OpenIncidentRow> OpenIncidents { get; private set; } = [];
    public int UpCount { get; private set; }
    public int DownCount { get; private set; }
    public int PausedCount { get; private set; }
    public int UnknownCount { get; private set; }
    public double? OverallUptime24h { get; private set; }
    /// <summary>BIZ-3 quota meters.</summary>
    public PlanUsage Usage { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        CustomerName = (await current.GetCustomerAsync(ct)).Name;

        var monitors = await db.Monitors.AsNoTracking()
            .Where(m => m.CustomerId == cid)
            .OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.Type, m.Target, m.Status, m.LastResponseTimeMs, m.LastCheckedAtUtc })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var summaries = await stats.GetSummariesAsync(monitors.Select(m => m.Id), now.AddHours(-24), now, ct);

        Monitors = monitors.Select(m => new Row(m.Id, m.Name, m.Type, m.Target, m.Status, m.LastResponseTimeMs, m.LastCheckedAtUtc,
            summaries.TryGetValue(m.Id, out var s) ? s.UptimePercent : null)).ToList();

        UpCount = Monitors.Count(m => m.Status == MonitorStatus.Up);
        DownCount = Monitors.Count(m => m.Status == MonitorStatus.Down);
        PausedCount = Monitors.Count(m => m.Status == MonitorStatus.Paused);
        UnknownCount = Monitors.Count(m => m.Status == MonitorStatus.Unknown);

        var total = summaries.Values.Sum(s => s.TotalChecks);
        var up = summaries.Values.Sum(s => s.UpChecks);
        OverallUptime24h = total > 0 ? Math.Round(100.0 * up / total, 3) : null;

        OpenIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.Monitor.CustomerId == cid && i.ResolvedAtUtc == null)
            .OrderByDescending(i => i.StartedAtUtc)
            .Select(i => new OpenIncidentRow(i.Id, i.MonitorId, i.Monitor.Name, i.StartedAtUtc, i.Cause))
            .ToListAsync(ct);

        Usage = await usage.GetAsync(cid, ct);
    }
}
