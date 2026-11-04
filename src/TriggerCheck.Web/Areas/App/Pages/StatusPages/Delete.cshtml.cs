using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

[Authorize(Policy = Policies.CustomerAdmin)]
public class DeleteModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    public StatusPage StatusPage { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await db.StatusPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return NotFound();
        StatusPage = page;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return NotFound();
        db.StatusPages.Remove(page);
        await db.SaveChangesAsync(ct);
        await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        TempData["Success"] = L["Status page \"{0}\" was deleted.", page.Title].Value;
        return RedirectToPage("Index");
    }
}
