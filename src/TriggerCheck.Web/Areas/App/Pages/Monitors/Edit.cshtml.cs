using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current) : MonitorFormPageModel
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

        Input.Validate(Plan, ModelState);
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

        await db.SaveChangesAsync(ct);
        TempData["Success"] = $"\"{m.Name}\" monitörü güncellendi.";
        return RedirectToPage("Details", new { id });
    }
}
