using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Pages;

public class FeaturesModel(AppDbContext db, IConfiguration config) : PageModel
{
    public string ContactEmail => Marketing.ContactEmail(config);
    public int FastestInterval { get; private set; } = PlanRules.SupportedIntervals.Min();
    public int LongestRetention { get; private set; } = 30;

    public async Task OnGetAsync()
    {
        var plans = await Marketing.ActivePlansAsync(db, HttpContext.RequestAborted);
        if (plans.Count == 0) return;
        FastestInterval = plans.Min(p => p.MinIntervalSeconds);
        LongestRetention = plans.Max(p => p.RetentionDays);
    }
}
