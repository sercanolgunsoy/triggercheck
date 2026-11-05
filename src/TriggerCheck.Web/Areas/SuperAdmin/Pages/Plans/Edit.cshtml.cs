using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.SuperAdmin.Services;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

[Authorize(Policy = Policies.SuperAdmin)]
public class EditModel(AppDbContext db, Text<AdminResource> L) : PageModel
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
        await Input.ValidateAsync(db, ModelState, L, currentId: id);
        if (!ModelState.IsValid) return Page();

        var oldMin = Plan.MinIntervalSeconds;
        Input.ApplyTo(Plan);

        var adjusted = 0;
        // The interval clamp is a set-based update: commit it together with the plan change.
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            if (Plan.MinIntervalSeconds > oldMin)
            {
                var customerIds = await db.Customers.Where(c => c.PlanId == id).Select(c => c.Id).ToListAsync();
                adjusted = await SuperAdminOps.ClampMonitorsAsync(db, Plan, customerIds);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var msg = L["Plan \"{0}\" was updated.", Plan.Name].Value;
        if (adjusted > 0)
            msg += " " + L["Check interval raised to the new plan minimum ({1}) for {0} monitor(s).", adjusted, Fmt.Interval(Plan.MinIntervalSeconds)].Value;
        TempData["Success"] = msg;

        var overLimit = await db.Customers.Where(c => c.PlanId == id && c.Monitors.Count > Plan.MaxMonitors).CountAsync();
        if (overLimit > 0)
            TempData["Error"] = L["Warning: {0} customer(s) exceed the new monitor limit ({1}). The monitors were not deleted; these customers cannot add new monitors.", overLimit, Plan.MaxMonitors].Value;
        return RedirectToPage("Index");
    }
}
