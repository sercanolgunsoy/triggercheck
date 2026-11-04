using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Heartbeat;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L, HeartbeatUrlReveal reveal) : MonitorFormPageModel
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
        var m = await commands.FindTrackedAsync(current.RequiredCustomerId, id, ct);
        if (m is null) return NotFound();
        Id = id;
        Plan = (await current.GetCustomerAsync(ct)).Plan;

        Input.Validate(Plan, ModelState, L);
        if (!ModelState.IsValid) return Page();

        // Version bump, probe-settings reset and the concurrency retry live in MonitorCommands.UpdateAsync.
        var r = await commands.UpdateAsync(m, Input, ct);
        switch (r.Error)
        {
            case MonitorCommandError.NotFound:
                return NotFound(); // deleted meanwhile
            case MonitorCommandError.Conflict:
                ModelState.AddModelError("", L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."]);
                return Page();
        }
        TempData["Success"] = L["Monitor \"{0}\" was updated.", m.Name].Value;
        if (r.IssuedHeartbeatToken is { } token)
            return RedirectToPage("Details", new { id, reveal = reveal.Park(User, current.RequiredCustomerId, id, HeartbeatEndpoint.PingUrl(Request, token)) });
        return RedirectToPage("Details", new { id });
    }
}
