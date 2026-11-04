using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Ai;

/// <summary>
/// Records the current user's helpful / not helpful vote on a succeeded AI decision (posted by the _AiInterpretation
/// partial). One vote per user and decision (a new vote replaces the old one); the decision mirrors the latest vote.
/// Any customer member may vote (viewers too: it changes no monitoring data). Tenant-checked: another customer's
/// decision, or one without AI content, is a 404.
/// </summary>
public class FeedbackModel(AppDbContext db, ICurrentCustomer current, UserManager<ApplicationUser> users, TimeProvider time, Text<AppResource> L) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Ai/Index");

    public async Task<IActionResult> OnPostAsync(long decisionId, int value, string? returnUrl, CancellationToken ct)
    {
        if (value is not (1 or -1)) return BadRequest();
        var cid = current.RequiredCustomerId;
        var decision = await db.AiDecisions.FirstOrDefaultAsync(d => d.Id == decisionId && d.CustomerId == cid && d.Status == AiDecisionStatus.Succeeded, ct);
        var userId = users.GetUserId(User);
        if (decision is null || userId is null) return NotFound();

        var now = time.GetUtcNow().UtcDateTime;
        var vote = await db.AiFeedback.FirstOrDefaultAsync(f => f.DecisionId == decisionId && f.UserId == userId, ct);
        if (vote is null) db.AiFeedback.Add(vote = new AiFeedback { DecisionId = decisionId, UserId = userId, CustomerId = cid });
        vote.Value = value;
        vote.AtUtc = now;
        decision.UserFeedback = value;
        decision.FeedbackByUserId = userId;
        decision.FeedbackAtUtc = now;
        await db.SaveChangesAsync(ct);

        TempData["Success"] = L["Thank you for your feedback."].Value;
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage("/Ai/Index");
    }
}
