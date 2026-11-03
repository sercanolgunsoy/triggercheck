using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : MonitorFormPageModel
{
    public int CurrentCount { get; private set; }
    public bool QuotaReached => !PlanRules.CanAddMonitor(Plan, CurrentCount);

    private async Task LoadAsync(CancellationToken ct)
    {
        Plan = (await current.GetCustomerAsync(ct)).Plan;
        CurrentCount = await db.Monitors.CountAsync(m => m.CustomerId == current.RequiredCustomerId, ct);
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
        var cid = current.RequiredCustomerId;

        if (QuotaReached)
        {
            ModelState.AddModelError("", L["You have reached the monitor limit of your plan ({0}). Upgrade your plan to add a new monitor.", Plan.MaxMonitors]);
            return Page();
        }

        Input.Validate(Plan, ModelState, L);
        if (!ModelState.IsValid) return Page();

        var now = DateTime.UtcNow;
        var monitor = new MonitorEntity
        {
            CustomerId = cid,
            IsEnabled = true,
            Status = Domain.Entities.MonitorStatus.Unknown,
            CreatedAtUtc = now,
            StatusChangedAtUtc = now,
            NextCheckAtUtc = now
        };
        Input.ApplyTo(monitor);
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync(ct);

        TempData["Success"] = L["Monitor \"{0}\" was created. The first check will run shortly.", monitor.Name].Value;
        return RedirectToPage("Details", new { id = monitor.Id });
    }
}
