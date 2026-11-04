using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Maintenance;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
{
    [BindProperty] public MaintenanceInput Input { get; set; } = new();
    public int Id { get; private set; }
    public IReadOnlyList<(int Id, string Name)> Monitors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var w = await db.MaintenanceWindows.AsNoTracking().Include(x => x.Monitors)
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (w is null) return NotFound();
        Id = id;
        Input = MaintenanceInput.From(w);
        Monitors = await CreateModel.MonitorChoicesAsync(db, cid, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var w = await db.MaintenanceWindows.Include(x => x.Monitors).FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (w is null) return NotFound();
        Id = id;

        var monitors = await Input.ValidateAsync(ModelState, L, db, cid, time.GetUtcNow().UtcDateTime, ct);
        if (!ModelState.IsValid)
        {
            Monitors = await CreateModel.MonitorChoicesAsync(db, cid, ct);
            return Page();
        }

        Input.ApplyTo(w, monitors);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Maintenance window \"{0}\" was updated.", w.Name].Value;
        return RedirectToPage("Index");
    }
}
