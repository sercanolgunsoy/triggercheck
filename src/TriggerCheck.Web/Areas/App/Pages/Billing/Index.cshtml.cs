using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Billing;

/// <summary>
/// BIZ-2/BIZ-3 billing page: plan, billing state, quota usage and payment history for every customer member; plan
/// changes and cancellation for CustomerAdmins only.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, BillingService billing, PlanUsageService usage, IPaymentProvider provider,
    Text<AppResource> L) : PageModel
{
    public Customer Customer { get; private set; } = null!;
    public Subscription? Subscription { get; private set; }
    public PlanUsage Usage { get; private set; } = null!;
    public List<Plan> Plans { get; private set; } = [];
    public List<PaymentEvent> History { get; private set; } = [];
    public bool PaymentsAvailable => provider.IsConfigured;
    /// <summary>"success" / "failed" after returning from the hosted checkout.</summary>
    public string? Checkout { get; private set; }

    public async Task OnGetAsync(string? checkout, CancellationToken ct)
    {
        Checkout = checkout is "success" or "failed" ? checkout : null;
        await LoadAsync(ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        Customer = await current.GetCustomerAsync(ct);
        Subscription = await billing.CurrentSubscriptionAsync(cid, ct);
        Usage = await usage.GetAsync(cid, ct);
        Plans = await billing.PurchasablePlansAsync(ct);
        History = await db.PaymentEvents.AsNoTracking().Where(e => e.CustomerId == cid)
            .OrderByDescending(e => e.OccurredAtUtc).Take(50).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostChangePlanAsync(int planId, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var r = await billing.ChangePlanAsync(current.RequiredCustomerId, planId, ct);
        if (r.Succeeded)
        {
            var msg = L["Your plan was changed."].Value;
            if (r.AdjustedMonitors > 0) msg += " " + L["Check interval raised to the new plan minimum for {0} monitor(s).", r.AdjustedMonitors].Value;
            TempData["Success"] = msg;
        }
        else TempData["Error"] = ErrorText(r);
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var r = await billing.CancelAsync(current.RequiredCustomerId, ct);
        if (r.Succeeded) TempData["Success"] = L["Your subscription was cancelled. Your plan stays active until the end of the paid period."].Value;
        else TempData["Error"] = ErrorText(r);
        return RedirectToPage("Index");
    }

    /// <summary>Customer-facing text for a failed billing operation (provider details are only logged).</summary>
    public string ErrorText(BillingOutcome r) => r.Error switch
    {
        BillingError.TooManyMonitors => L["The new plan allows fewer monitors than you have. Delete {0} monitor(s) first, then change the plan.", r.MonitorsToDelete].Value,
        BillingError.PlanNotAvailable => L["This plan cannot be chosen."].Value,
        BillingError.ProviderUnavailable => L["Online payment is not available at the moment. Please contact us."].Value,
        BillingError.ProviderFailed => L["The payment provider did not accept the request. Please try again later."].Value,
        _ => L["This action is not possible in the current subscription state."].Value
    };
}
