using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current) : MonitorFormPageModel
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
            ModelState.AddModelError("", $"Planınızın monitör limitine ulaştınız ({Plan.MaxMonitors}). Yeni monitör eklemek için planınızı yükseltin.");
            return Page();
        }

        Input.Validate(Plan, ModelState);
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

        TempData["Success"] = $"\"{monitor.Name}\" monitörü oluşturuldu. İlk kontrol birazdan yapılacak.";
        return RedirectToPage("Details", new { id = monitor.Id });
    }
}
