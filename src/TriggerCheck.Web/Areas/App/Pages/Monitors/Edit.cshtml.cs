using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Heartbeat;
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

        // Bumping Version lets an in-flight check notice the edit (it discards results for changed probe settings).
        // If a check or a pause changed the row since we read it, the save fails on the token: reload the fresh row
        // and re-apply the form. Form fields are intentionally last-writer-wins between concurrent admins (no
        // "someone else edited this" check); runtime state written by checks or a pause is preserved.
        for (var attempt = 1; ; attempt++)
        {
            ApplyInput(m);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                await db.Entry(m).ReloadAsync(ct);
                if (db.Entry(m).State == EntityState.Detached) return NotFound(); // deleted meanwhile
            }
            catch (DbUpdateConcurrencyException)
            {
                ModelState.AddModelError("", L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."]);
                return Page();
            }
        }
        TempData["Success"] = L["Monitor \"{0}\" was updated.", m.Name].Value;
        if (Input.IssuedHeartbeatToken is { } token) TempData[DetailsModel.HeartbeatUrlKey] = HeartbeatEndpoint.PingUrl(Request, token);
        return RedirectToPage("Details", new { id });
    }

    private void ApplyInput(Domain.Entities.Monitor m)
    {
        var before = ProbeSettings.Of(m);
        var intervalChanged = m.IntervalSeconds != Input.IntervalSeconds;
        var becameHeartbeat = m.Type != MonitorType.Heartbeat && Input.Type == MonitorType.Heartbeat;
        Input.ApplyTo(m);
        var probeChanged = ProbeSettings.Of(m) != before;

        if (probeChanged)
        {
            // Something else is being checked now: start over with a clean failure streak, and don't send an UP
            // alert for the new target just because the old one was down when the monitor was paused.
            m.ConsecutiveFailures = 0;
            m.DownWhenPaused = false;
        }
        if (m.Status != MonitorStatus.Paused && (probeChanged || intervalChanged))
            m.NextCheckAtUtc = m.Type == MonitorType.Heartbeat ? HeartbeatSchedule.AfterSettingsChange(m, becameHeartbeat, DateTime.UtcNow) : DateTime.UtcNow;
        m.Version++;
    }
}
