using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current) : PageModel
{
    [BindProperty] public AlertChannelInput Input { get; set; } = new();

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Input.Validate(ModelState);
        if (!ModelState.IsValid) return Page();

        var c = new AlertChannel { CustomerId = current.RequiredCustomerId };
        Input.ApplyTo(c);
        db.AlertChannels.Add(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = $"\"{c.Name}\" bildirim kanalı eklendi.";
        return RedirectToPage("Index");
    }
}
