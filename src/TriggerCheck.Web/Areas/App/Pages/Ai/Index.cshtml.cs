using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Ai;

/// <summary>
/// AI settings of the customer: opt-in switch (CustomerAdmin; decision K2), privacy note (K5), plan features and this
/// month's AI quota (AI-10), and the latest AI results with feedback. Viewers see everything read-only.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, IAiUsage usage, UserManager<ApplicationUser> users, Text<AppResource> L) : PageModel
{
    public const int RecentCount = 10;

    public Customer Customer { get; private set; } = null!;
    public AiUsageSnapshot Usage { get; private set; } = null!;
    public List<AiContentModel> Recent { get; private set; } = [];
    public bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Customer = await current.GetCustomerAsync(ct);
        Usage = await usage.GetMonthlyUsageAsync(Customer.Id, ct);
        var returnUrl = Url.Page("/Ai/Index", new { area = "App" }) ?? "/App/Ai";
        var decisions = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == Customer.Id && d.Status == AiDecisionStatus.Succeeded)
            .OrderByDescending(d => d.Id)
            .Take(RecentCount)
            .ToListAsync(ct);
        // The buttons show the current user's own vote.
        var ids = decisions.Select(d => d.Id).ToList();
        var userId = users.GetUserId(User);
        var mine = await db.AiFeedback.AsNoTracking()
            .Where(f => ids.Contains(f.DecisionId) && f.UserId == userId && f.CustomerId == Customer.Id)
            .ToDictionaryAsync(f => f.DecisionId, f => f.Value, ct);
        Recent = decisions.Select(d => AiContentModel.From(d, returnUrl, mine.TryGetValue(d.Id, out var v) ? v : null)).ToList();
    }

    public async Task<IActionResult> OnPostAsync(bool enabled, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var cid = current.RequiredCustomerId;
        var customer = await db.Customers.FirstAsync(c => c.Id == cid, ct);
        customer.AiEnabled = enabled;
        // Background summaries (AI-2 / AI-3) are written in the language of the admin who switched AI on.
        if (enabled) customer.AiLanguage = Fmt.IsTurkish ? "tr" : "en";
        await db.SaveChangesAsync(ct);
        TempData["Success"] = (enabled ? L["AI features were enabled."] : L["AI features were disabled."]).Value;
        return RedirectToPage();
    }

    /// <summary>Language of AI summaries generated in the background (CustomerAdmin).</summary>
    public async Task<IActionResult> OnPostLanguageAsync(string? language, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (language is not ("en" or "tr")) return BadRequest();
        var cid = current.RequiredCustomerId;
        var customer = await db.Customers.FirstAsync(c => c.Id == cid, ct);
        customer.AiLanguage = language;
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["The AI summary language was saved."].Value;
        return RedirectToPage();
    }

    public string FeatureName(string feature) => feature switch
    {
        AiFeatures.IncidentInterpretation => L["Incident summaries and likely causes"],
        AiFeatures.SlowdownExplanation => L["Slowdown explanations"],
        AiFeatures.SettingsSuggestions => L["Monitor setting suggestions"],
        AiFeatures.CorrelationGrouping => L["Grouping of related incidents"],
        AiFeatures.NarrativeReport => L["Weekly and monthly reports"],
        AiFeatures.QuestionAnswer => L["Questions in natural language"],
        AiFeatures.PostmortemDraft => L["Postmortem and status page drafts"],
        _ => feature
    };
}
