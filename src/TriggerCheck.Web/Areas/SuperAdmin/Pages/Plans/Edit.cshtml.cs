using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

[Authorize(Policy = Policies.SuperAdmin)]
public class EditModel(AppDbContext db) : PageModel
{
    [BindProperty] public PlanInput Input { get; set; } = new();
    public Plan Plan { get; private set; } = null!;
    public int CustomerCount { get; private set; }
    public int MonitorCount { get; private set; }

    private async Task<bool> LoadAsync(int id)
    {
        var p = await db.Plans.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return false;
        Plan = p;
        CustomerCount = await db.Customers.CountAsync(c => c.PlanId == id);
        MonitorCount = await db.Monitors.CountAsync(m => m.Customer.PlanId == id);
        return true;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Input = PlanInput.From(Plan);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id)) return NotFound();
        await Input.ValidateAsync(db, ModelState, currentId: id);
        if (!ModelState.IsValid) return Page();

        var oldMin = Plan.MinIntervalSeconds;
        Input.ApplyTo(Plan);

        var adjusted = 0;
        if (Plan.MinIntervalSeconds > oldMin)
        {
            var customerIds = await db.Customers.Where(c => c.PlanId == id).Select(c => c.Id).ToListAsync();
            adjusted = await SuperAdminOps.ClampMonitorsAsync(db, Plan, customerIds);
        }
        await db.SaveChangesAsync();

        var msg = $"\"{Plan.Name}\" planı güncellendi.";
        if (adjusted > 0)
            msg += $" {adjusted} monitörün kontrol aralığı yeni plan minimumuna ({PlanRules.FormatInterval(Plan.MinIntervalSeconds)}) yükseltildi.";
        TempData["Success"] = msg;

        var overLimit = await db.Customers.Where(c => c.PlanId == id && c.Monitors.Count > Plan.MaxMonitors).CountAsync();
        if (overLimit > 0)
            TempData["Error"] = $"Uyarı: {overLimit} müşterinin monitör sayısı yeni sınırı ({Plan.MaxMonitors}) aşıyor. Monitörler silinmedi; bu müşteriler yeni monitör ekleyemez.";
        return RedirectToPage("Index");
    }
}
