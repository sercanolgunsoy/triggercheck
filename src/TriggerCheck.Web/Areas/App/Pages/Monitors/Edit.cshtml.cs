using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current, Text<AppResource> L) : MonitorFormPageModel
{
    public int Id { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (m is null) return NotFound();
        Id = id;
        Plan = (await current.GetCustomerAsync(ct)).Plan;
        Input = MonitorInput.From(m);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var m = await db.Monitors.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (m is null) return NotFound();
        Id = id;
        Plan = (await current.GetCustomerAsync(ct)).Plan;

        Input.Validate(Plan, ModelState, L);
        if (!ModelState.IsValid) return Page();

        var probeChanged = m.Type != Input.Type || m.Target != Input.Target || m.Port != (Input.Type == MonitorType.Tcp ? Input.Port : null);
        var intervalChanged = m.IntervalSeconds != Input.IntervalSeconds;
        Input.ApplyTo(m);

        if (m.Status != MonitorStatus.Paused)
        {
            if (probeChanged)
            {
                // A different endpoint: start over with a clean state.
                m.ConsecutiveFailures = 0;
            }
            if (probeChanged || intervalChanged) m.NextCheckAtUtc = DateTime.UtcNow;
        }

        // Concurrency token: an in-flight check re-reads the monitor and won't overwrite this edit.
        m.Version++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            ModelState.AddModelError("", L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."]);
            return Page();
        }
        TempData["Success"] = L["Monitor \"{0}\" was updated.", m.Name].Value;
        return RedirectToPage("Details", new { id });
    }
}
