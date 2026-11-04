using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Refunds;

/// <summary>
/// Paid subscriptions that were refused (second subscription / plan mismatch) and cancelled at the provider. They stay
/// "open" until an operator has refunded them in the iyzico panel and marks them here (who and when are recorded).
/// </summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db, TimeProvider time, Text<AdminResource> L, ILogger<IndexModel> logger) : PageModel
{
    public record Row(int Id, int CustomerId, string CustomerName, string PlanName, string? Reference, DateTime NeededAtUtc, DateTime? HandledAtUtc, string? HandledBy);

    public List<Row> Open { get; private set; } = [];
    public List<Row> Handled { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var rows = await db.Subscriptions.AsNoTracking().Where(s => s.RefundNeededAtUtc != null)
            .OrderByDescending(s => s.RefundNeededAtUtc)
            .Select(s => new Row(s.Id, s.CustomerId, s.Customer.Name, s.Plan.Name, s.ProviderReference, s.RefundNeededAtUtc!.Value, s.RefundHandledAtUtc, s.RefundHandledBy))
            .Take(200).ToListAsync();
        Open = rows.Where(r => r.HandledAtUtc is null).ToList();
        Handled = rows.Where(r => r.HandledAtUtc is not null).Take(50).ToList();
    }

    public async Task<IActionResult> OnPostMarkRefundedAsync(int id)
    {
        var by = User.Identity?.Name ?? "?";
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Subscriptions.Where(s => s.Id == id && s.RefundNeededAtUtc != null && s.RefundHandledAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RefundHandledAtUtc, now).SetProperty(s => s.RefundHandledBy, by));
        if (updated == 0) return NotFound();
        logger.LogInformation("Audit: refund of subscription {SubscriptionId} marked as done by {User}.", id, by);
        TempData["Success"] = L["The refund was marked as done."].Value;
        return RedirectToPage();
    }
}
