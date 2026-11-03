using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Monitors;

[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db) : PageModel
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
            var like = $"%{Q.Trim()}%";
            q = q.Where(m => EF.Functions.Like(m.Target, like) || EF.Functions.Like(m.Name, like));
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

    public async Task<IActionResult> OnPostPauseAsync(int id) => await SetPausedAsync(id, paused: true);
    public async Task<IActionResult> OnPostResumeAsync(int id) => await SetPausedAsync(id, paused: false);

    private async Task<IActionResult> SetPausedAsync(int id, bool paused)
    {
        var m = await db.Monitors.FindAsync(id);
        if (m is null) return NotFound();
        var now = DateTime.UtcNow;
        if (paused && m.IsEnabled)
        {
            m.IsEnabled = false;
            m.Status = MonitorStatus.Paused;
            m.StatusChangedAtUtc = now;
            TempData["Success"] = $"\"{m.Name}\" duraklatıldı.";
        }
        else if (!paused && !m.IsEnabled)
        {
            m.IsEnabled = true;
            m.Status = MonitorStatus.Unknown;
            m.ConsecutiveFailures = 0;
            m.StatusChangedAtUtc = now;
            m.NextCheckAtUtc = now;
            TempData["Success"] = $"\"{m.Name}\" yeniden başlatıldı.";
        }
        await db.SaveChangesAsync();
        return RedirectToPage(new { CustomerId, Status, Type, Q, p = PageNo });
    }
}
