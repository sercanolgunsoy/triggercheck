using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Monitors;

[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db, MonitorLifecycle lifecycle, Text<AdminResource> L) : PageModel
{
    public const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public int? CustomerId { get; set; }
    [BindProperty(SupportsGet = true)] public MonitorStatus? Status { get; set; }
    [BindProperty(SupportsGet = true)] public MonitorType? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNo { get; set; } = 1;

    public List<(int Id, string Name)> Customers { get; private set; } = [];
    public List<Row> Rows { get; private set; } = [];
    public int Total { get; private set; }
    public int PageCount => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public record Row(int Id, string Name, MonitorType Type, string Target, int? Port, int IntervalSeconds, bool IsEnabled,
        MonitorStatus Status, DateTime? LastCheckedAtUtc, int? LastResponseTimeMs, string? LastError,
        int CustomerId, string CustomerName, bool CustomerActive);

    public async Task OnGetAsync()
    {
        Customers = (await db.Customers.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync())
            .Select(c => (c.Id, c.Name)).ToList();

        var q = db.Monitors.AsNoTracking().AsQueryable();
        if (CustomerId is int cid) q = q.Where(m => m.CustomerId == cid);
        if (Status is MonitorStatus s) q = q.Where(m => m.Status == s);
        if (Type is MonitorType t) q = q.Where(m => m.Type == t);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            // Case-insensitive on both providers (Postgres LIKE is case-sensitive, SQLite's is not).
            var term = Q.Trim().ToLowerInvariant();
            q = q.Where(m => m.Target.ToLower().Contains(term) || m.Name.ToLower().Contains(term));
        }

        Total = await q.CountAsync();
        if (PageNo < 1) PageNo = 1;
        if (PageNo > PageCount) PageNo = PageCount;

        Rows = await q.OrderBy(m => m.Customer.Name).ThenBy(m => m.Name).ThenBy(m => m.Id)
            .Skip((PageNo - 1) * PageSize).Take(PageSize)
            .Select(m => new Row(m.Id, m.Name, m.Type, m.Target, m.Port, m.IntervalSeconds, m.IsEnabled, m.Status,
                m.LastCheckedAtUtc, m.LastResponseTimeMs, m.LastError, m.CustomerId, m.Customer.Name, m.Customer.IsActive))
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostPauseAsync(int id, CancellationToken ct) => await SetPausedAsync(id, paused: true, ct);
    public async Task<IActionResult> OnPostResumeAsync(int id, CancellationToken ct) => await SetPausedAsync(id, paused: false, ct);

    private async Task<IActionResult> SetPausedAsync(int id, bool paused, CancellationToken ct)
    {
        // Same semantics as the customer panel (closes open incidents on pause), see MonitorLifecycle.
        MonitorLifecycleResult? r;
        try
        {
            r = paused ? await lifecycle.PauseAsync(id, customerId: null, ct) : await lifecycle.ResumeAsync(id, customerId: null, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            TempData["Error"] = L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."].Value;
            return RedirectToPage(new { CustomerId, Status, Type, Q, p = PageNo });
        }
        if (r is null) return NotFound();
        if (r.Changed)
            TempData["Success"] = (paused ? L["\"{0}\" was paused.", r.Name] : L["\"{0}\" was resumed.", r.Name]).Value;
        return RedirectToPage(new { CustomerId, Status, Type, Q, p = PageNo });
    }
}
