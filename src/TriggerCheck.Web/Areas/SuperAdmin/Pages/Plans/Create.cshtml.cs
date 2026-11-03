using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

[Authorize(Policy = Policies.SuperAdmin)]
public class CreateModel(AppDbContext db, Text<AdminResource> L) : PageModel
{
    [BindProperty] public PlanInput Input { get; set; } = new() { MinIntervalSeconds = 300, MaxMonitors = 10, RetentionDays = 30, MonthlyPrice = 0 };

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await Input.ValidateAsync(db, ModelState, L, currentId: null);
        if (!ModelState.IsValid) return Page();

        var plan = new Plan();
        Input.ApplyTo(plan);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        TempData["Success"] = L["Plan \"{0}\" was created.", plan.Name].Value;
        return RedirectToPage("Index");
    }
}
