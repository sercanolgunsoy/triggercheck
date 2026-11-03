using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Plans;

[Authorize(Policy = Policies.SuperAdmin)]
public class CreateModel(AppDbContext db) : PageModel
{
    [BindProperty] public PlanInput Input { get; set; } = new() { MinIntervalSeconds = 300, MaxMonitors = 10, RetentionDays = 30, MonthlyPrice = 0 };

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        await Input.ValidateAsync(db, ModelState, currentId: null);
        if (!ModelState.IsValid) return Page();

        var plan = new Plan();
        Input.ApplyTo(plan);
        db.Plans.Add(plan);
        await db.SaveChangesAsync();
        TempData["Success"] = $"\"{plan.Name}\" planı oluşturuldu.";
        return RedirectToPage("Index");
    }
}
