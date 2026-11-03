using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    [BindProperty] public AlertChannelInput Input { get; set; } = new();
    public int Id { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        Id = id;
        Input = AlertChannelInput.From(c);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        Id = id;

        Input.Validate(ModelState, L);
        if (!ModelState.IsValid) return Page();

        Input.ApplyTo(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Alert channel \"{0}\" was updated.", c.Name].Value;
        return RedirectToPage("Index");
    }
}
