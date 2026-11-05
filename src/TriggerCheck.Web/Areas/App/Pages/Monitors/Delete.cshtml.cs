using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class DeleteModel(AppDbContext db, MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L) : PageModel
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
        // Monitor and notifications are removed in one transaction (history by cascade); see MonitorCommands.DeleteAsync.
        var name = await commands.DeleteAsync(current.RequiredCustomerId, id, ct);
        if (name is null) return NotFound();

        TempData["Success"] = L["Monitor \"{0}\" was deleted.", name].Value;
        return RedirectToPage("Index");
    }
}
