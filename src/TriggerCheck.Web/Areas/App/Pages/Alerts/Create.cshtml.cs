using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    [BindProperty] public AlertChannelInput Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Input.Validate(ModelState, L);
        if (!ModelState.IsValid) return Page();

        var c = new AlertChannel { CustomerId = current.RequiredCustomerId };
        Input.ApplyTo(c);
        db.AlertChannels.Add(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Alert channel \"{0}\" was added.", c.Name].Value;
        return RedirectToPage("Index");
    }
}
