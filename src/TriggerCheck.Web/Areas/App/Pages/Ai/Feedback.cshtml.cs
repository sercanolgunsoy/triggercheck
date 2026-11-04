using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Ai;

/// <summary>
/// Records helpful / not helpful feedback on an AI decision (posted by the _AiInterpretation partial). Any customer
/// member may give feedback (viewers too: it changes no monitoring data). Tenant-checked: another customer's
/// decision is a 404.
/// </summary>
public class FeedbackModel(AppDbContext db, ICurrentCustomer current, UserManager<ApplicationUser> users, TimeProvider time, Text<AppResource> L) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Ai/Index");

    public async Task<IActionResult> OnPostAsync(long decisionId, int value, string? returnUrl, CancellationToken ct)
    {
        if (value is not (1 or -1)) return BadRequest();
        var cid = current.RequiredCustomerId;
        var decision = await db.AiDecisions.FirstOrDefaultAsync(d => d.Id == decisionId && d.CustomerId == cid, ct);
        if (decision is null) return NotFound();

        decision.UserFeedback = value;
        decision.FeedbackByUserId = users.GetUserId(User);
        decision.FeedbackAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        TempData["Success"] = L["Thank you for your feedback."].Value;
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Ai/Index");
    }
}
