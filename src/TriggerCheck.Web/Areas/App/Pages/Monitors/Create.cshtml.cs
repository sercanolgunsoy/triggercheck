using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TriggerCheck.Domain;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L) : MonitorFormPageModel
{
    public int CurrentCount { get; private set; }
    public bool QuotaReached => !PlanRules.CanAddMonitor(Plan, CurrentCount);

    private async Task LoadAsync(CancellationToken ct)
    {
        Plan = (await current.GetCustomerAsync(ct)).Plan;
        CurrentCount = await commands.CountAsync(current.RequiredCustomerId, ct);
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        Input.IntervalSeconds = Plan.MinIntervalSeconds;
        if (!PlanRules.IsIntervalAllowed(Plan, Input.IntervalSeconds)) Input.IntervalSeconds = PlanRules.AllowedIntervals(Plan).FirstOrDefault(300);
        if (Input.TimeoutSeconds >= Input.IntervalSeconds) Input.TimeoutSeconds = Math.Max(1, Input.IntervalSeconds / 2);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        if (QuotaReached)
        {
            ModelState.AddModelError("", L["You have reached the monitor limit of your plan ({0}). Upgrade your plan to add a new monitor.", Plan.MaxMonitors]);
            return Page();
        }

        Input.Validate(Plan, ModelState, L);
        if (!ModelState.IsValid) return Page();

        // The quota re-check and the insert run atomically in MonitorCommands (parallel posts cannot exceed the quota).
        var r = await commands.CreateAsync(current.RequiredCustomerId, Plan, Input, ct);
        switch (r.Error)
        {
            case MonitorCommandError.QuotaReached:
                CurrentCount = await commands.CountAsync(current.RequiredCustomerId, ct);
                ModelState.AddModelError("", L["You have reached the monitor limit of your plan ({0}). Upgrade your plan to add a new monitor.", Plan.MaxMonitors]);
                return Page();
            case MonitorCommandError.Busy:
                ModelState.AddModelError("", L["The system is busy right now. Please try again in a moment."]);
                return Page();
        }

        TempData["Success"] = L["Monitor \"{0}\" was created. The first check will run shortly.", r.Monitor!.Name].Value;
        return RedirectToPage("Details", new { id = r.Monitor.Id });
    }
}
