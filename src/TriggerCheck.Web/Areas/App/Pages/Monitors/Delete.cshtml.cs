using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class DeleteModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : PageModel
{
    public MonitorEntity Monitor { get; private set; } = null!;
    public int CheckCount { get; private set; }
    public int IncidentCount { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (m is null) return NotFound();
        Monitor = m;
        CheckCount = await db.CheckResults.CountAsync(c => c.MonitorId == id, ct);
        IncidentCount = await db.Incidents.CountAsync(i => i.MonitorId == id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (m is null) return NotFound();

        // Notifications reference the monitor without an FK; remove them as well.
        await db.Notifications.Where(n => n.MonitorId == id).ExecuteDeleteAsync(ct);
        // Set-based delete: not subject to the Version concurrency token (a concurrent pause/check must not make
        // the delete fail). CheckResults and Incidents are removed by the database cascade.
        await db.Monitors.Where(x => x.Id == id && x.CustomerId == cid).ExecuteDeleteAsync(ct);

        TempData["Success"] = L["Monitor \"{0}\" was deleted.", m.Name].Value;
        return RedirectToPage("Index");
    }
}
