using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

public class IndexModel(AppDbContext db, ICurrentCustomer current, IUptimeStatsService stats) : PageModel
{
    public record Row(int Id, string Name, MonitorType Type, string Target, int? Port, MonitorStatus Status, int IntervalSeconds,
        int? LastResponseTimeMs, DateTime? LastCheckedAtUtc, double? Uptime24h, DnsRecordType? DnsRecordType = null);

    [BindProperty(SupportsGet = true)] public MonitorStatus? Status { get; set; }

    public List<Row> Monitors { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int MaxMonitors { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        MaxMonitors = (await current.GetCustomerAsync(ct)).Plan.MaxMonitors;

        var q = db.Monitors.AsNoTracking().Where(m => m.CustomerId == cid);
        TotalCount = await q.CountAsync(ct);
        if (Status is MonitorStatus s) q = q.Where(m => m.Status == s);

        var list = await q.OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.Type, m.Target, m.Port, m.Status, m.IntervalSeconds, m.LastResponseTimeMs, m.LastCheckedAtUtc, m.DnsRecordType })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var sums = await stats.GetSummariesAsync(list.Select(m => m.Id), now.AddHours(-24), now, ct);
        Monitors = list.Select(m => new Row(m.Id, m.Name, m.Type, m.Target, m.Port, m.Status, m.IntervalSeconds, m.LastResponseTimeMs,
            m.LastCheckedAtUtc, sums.TryGetValue(m.Id, out var x) ? x.UptimePercent : null, m.DnsRecordType)).ToList();
    }
}
