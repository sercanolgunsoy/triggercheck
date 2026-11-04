using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : StatusPageFormModel(db)
{
    public int Id { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await Db.StatusPages.AsNoTracking().Include(p => p.Monitors).FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return NotFound();
        Id = id;
        Input = StatusPageInput.From(page);
        await LoadMonitorRowsAsync(cid, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await Db.StatusPages.Include(p => p.Monitors).FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return NotFound();
        Id = id;
        var monitors = await Input.ValidateAsync(ModelState, L, Db, cid, id, ct);
        if (ModelState.IsValid)
        {
            Input.ApplyTo(page, monitors);
            try
            {
                await Db.SaveChangesAsync(ct);
                await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
                TempData["Success"] = L["Status page \"{0}\" was updated.", page.Title].Value;
                return RedirectToPage("Index");
            }
            catch (DbUpdateException)
            {
                ModelState.AddModelError("Input.Slug", L["This address is already taken."]);
            }
        }
        await LoadMonitorRowsAsync(cid, ct);
        return Page();
    }
}
