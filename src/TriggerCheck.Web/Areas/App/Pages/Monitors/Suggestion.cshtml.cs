using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

/// <summary>
/// Apply / dismiss an AI-5 settings suggestion (posted by the "_SettingsSuggestions" partial on the monitor details
/// page). CustomerAdmin only: viewers see suggestions but cannot act on them. Tenant-checked: another customer's
/// suggestion is a 404. Apply goes through the normal monitor validation (see <see cref="SettingsSuggestionCommands"/>).
/// </summary>
[Authorize(Policy = Policies.CustomerAdmin)]
public class SuggestionModel(SettingsSuggestionCommands suggestions, ICurrentCustomer current, UserManager<ApplicationUser> users, Text<AppResource> L) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Index");

    public async Task<IActionResult> OnPostApplyAsync(long suggestionId, CancellationToken ct)
    {
        var customer = await current.GetCustomerAsync(ct);
        if (!SettingsSuggestionCommands.Allowed(customer)) return NotFound();
        var r = await suggestions.ApplyAsync(customer.Id, customer.Plan, suggestionId, users.GetUserId(User), ct);
        switch (r.Outcome)
        {
            case SuggestionOutcome.NotFound:
                return NotFound();
            case SuggestionOutcome.Applied:
                TempData["Success"] = L["The suggestion was applied."].Value;
                break;
            case SuggestionOutcome.Stale:
                TempData["Error"] = L["This suggestion is out of date: the setting was changed in the meantime."].Value;
                break;
            case SuggestionOutcome.NotApplicable:
                TempData["Error"] = L["This suggestion has no value to apply. Edit the monitor settings instead."].Value;
                break;
            case SuggestionOutcome.Invalid:
                TempData["Error"] = L["The suggestion could not be applied: {0}", string.Join(" ", r.Errors ?? [])].Value;
                break;
            default:
                TempData["Error"] = L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."].Value;
                break;
        }
        return r.MonitorId > 0 ? RedirectToPage("Details", new { id = r.MonitorId }) : RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDismissAsync(long suggestionId, CancellationToken ct)
    {
        var customer = await current.GetCustomerAsync(ct);
        if (!SettingsSuggestionCommands.Allowed(customer)) return NotFound();
        var r = await suggestions.DismissAsync(customer.Id, suggestionId, users.GetUserId(User), ct);
        if (r.Outcome == SuggestionOutcome.NotFound) return NotFound();
        if (r.Outcome == SuggestionOutcome.Dismissed)
            TempData["Success"] = L["The suggestion was dismissed. It will not be shown again for 30 days."].Value;
        else
            TempData["Error"] = L["This suggestion is out of date: the setting was changed in the meantime."].Value;
        return RedirectToPage("Details", new { id = r.MonitorId });
    }
}
