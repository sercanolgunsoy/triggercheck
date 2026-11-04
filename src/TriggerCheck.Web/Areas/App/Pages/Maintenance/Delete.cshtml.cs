using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Maintenance;

[Authorize(Policy = Policies.CustomerAdmin)]
public class DeleteModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    public MaintenanceWindow Window { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var w = await db.MaintenanceWindows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (w is null) return NotFound();
        Window = w;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (w is null) return NotFound();
        db.MaintenanceWindows.Remove(w);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Maintenance window \"{0}\" was deleted.", w.Name].Value;
        return RedirectToPage("Index");
    }
}
