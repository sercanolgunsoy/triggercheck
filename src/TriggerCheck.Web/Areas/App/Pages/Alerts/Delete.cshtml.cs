using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class DeleteModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    public AlertChannel Channel { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        Channel = c;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        db.AlertChannels.Remove(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Alert channel \"{0}\" was deleted.", c.Name].Value;
        return RedirectToPage("Index");
    }
}
