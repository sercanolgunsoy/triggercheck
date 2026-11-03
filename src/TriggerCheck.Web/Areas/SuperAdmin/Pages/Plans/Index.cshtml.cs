using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db) : PageModel
{
    public List<Row> Rows { get; private set; } = [];

    public record Row(int Id, string Name, int MinIntervalSeconds, int MaxMonitors, int RetentionDays, decimal MonthlyPrice, bool IsActive, int Customers, int ActiveCustomers, int Monitors);

    public async Task OnGetAsync()
    {
        var plans = await db.Plans.AsNoTracking().ToListAsync();
        var cust = await db.Customers.GroupBy(c => c.PlanId)
            .Select(g => new { PlanId = g.Key, Total = g.Count(), Active = g.Count(c => c.IsActive) }).ToListAsync();
        var mons = await db.Monitors.GroupBy(m => m.Customer.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() }).ToListAsync();
        Rows = plans
            .OrderByDescending(p => p.MinIntervalSeconds).ThenBy(p => p.MonthlyPrice).ThenBy(p => p.Name)
            .Select(p => new Row(p.Id, p.Name, p.MinIntervalSeconds, p.MaxMonitors, p.RetentionDays, p.MonthlyPrice, p.IsActive,
                cust.FirstOrDefault(x => x.PlanId == p.Id)?.Total ?? 0,
                cust.FirstOrDefault(x => x.PlanId == p.Id)?.Active ?? 0,
                mons.FirstOrDefault(x => x.PlanId == p.Id)?.Count ?? 0))
            .ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var plan = await db.Plans.FindAsync(id);
        if (plan is null) return NotFound();
        var used = await db.Customers.CountAsync(c => c.PlanId == id);
        if (used > 0)
        {
            TempData["Error"] = $"\"{plan.Name}\" planı {used} müşteri tarafından kullanılıyor; silinemez. Önce müşterileri başka plana taşıyın ya da planı pasifleştirin.";
            return RedirectToPage();
        }
        db.Plans.Remove(plan);
        await db.SaveChangesAsync();
        TempData["Success"] = $"\"{plan.Name}\" planı silindi.";
        return RedirectToPage();
    }
}
