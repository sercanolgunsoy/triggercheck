using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Pages;

/// <summary>Public home page. Signed-in users go straight to their panel.</summary>
public class IndexModel(AppDbContext db, IConfiguration config) : PageModel
{
    public List<Plan> Plans { get; private set; } = [];
    public string ContactEmail => Marketing.ContactEmail(config);
    public int FastestInterval => Plans.Count > 0 ? Plans.Min(p => p.MinIntervalSeconds) : PlanRules.SupportedIntervals.Min();
    public int LongestRetention => Plans.Count > 0 ? Plans.Max(p => p.RetentionDays) : 30;

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.IsInRole(Roles.SuperAdmin)) return RedirectToPage("/Index", new { area = "SuperAdmin" });
        if (User.IsInRole(Roles.CustomerAdmin) || User.IsInRole(Roles.CustomerViewer)) return RedirectToPage("/Index", new { area = "App" });
        Plans = await Marketing.ActivePlansAsync(db, HttpContext.RequestAborted);
        return Page();
    }
}
