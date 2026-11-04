using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

/// <summary>Listing is visible to all customer members; the POST handlers are CustomerAdmin only.</summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, IOptionsMonitor<TwilioOptions> twilio, Text<AppResource> L) : PageModel
{
    public List<AlertChannel> Channels { get; private set; } = [];
    public string ContactEmail { get; private set; } = "";
    /// <summary>Customer setting: e-mail the contact address when no channel is enabled.</summary>
    public bool FallbackToContactEmail { get; private set; } = true;
    /// <summary>Twilio configured and the plan includes SMS / voice.</summary>
    public bool SmsVoiceAvailable { get; private set; }
    /// <summary>Enabled channels that can actually deliver (SMS / voice only when available).</summary>
    public bool AnyDeliverable => Channels.Any(c => c.IsEnabled && (SmsVoiceAvailable || !AlertDispatcher.IsSmsOrVoice(c.Type)));
    /// <summary>Enabled SMS / voice channels that are skipped because SMS / voice is not available.</summary>
    public bool HasUndeliverable => !SmsVoiceAvailable && Channels.Any(c => c.IsEnabled && AlertDispatcher.IsSmsOrVoice(c.Type));

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        Channels = await db.AlertChannels.AsNoTracking().Where(c => c.CustomerId == cid).OrderBy(c => c.Name).ToListAsync(ct);
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == cid)
            .Select(c => new { c.ContactEmail, c.AlertContactEmailWhenNoChannel }).SingleAsync(ct);
        ContactEmail = customer.ContactEmail;
        FallbackToContactEmail = customer.AlertContactEmailWhenNoChannel;
        SmsVoiceAvailable = await AlertChannelInput.SmsVoiceAvailableAsync(db, cid, twilio.CurrentValue, ct);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        if (!c.IsEnabled && AlertDispatcher.IsSmsOrVoice(c.Type) && !await AlertChannelInput.SmsVoiceAvailableAsync(db, cid, twilio.CurrentValue, ct))
        {
            TempData["Error"] = L["SMS and voice call alerts are not available for your plan."].Value;
            return RedirectToPage("Index");
        }
        c.IsEnabled = !c.IsEnabled;
        await db.SaveChangesAsync(ct);
        TempData["Success"] = (c.IsEnabled ? L["Channel \"{0}\" was enabled.", c.Name] : L["Channel \"{0}\" was disabled.", c.Name]).Value;
        return RedirectToPage("Index");
    }

    /// <summary>ENG-6: what happens to alerts when no channel is enabled.</summary>
    public async Task<IActionResult> OnPostFallbackAsync(bool? sendToContactEmail, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        if (sendToContactEmail is null) return BadRequest();
        var cid = current.RequiredCustomerId;
        await db.Customers.Where(c => c.Id == cid)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AlertContactEmailWhenNoChannel, sendToContactEmail.Value), ct);
        TempData["Success"] = L["The setting was saved."].Value;
        return RedirectToPage("Index");
    }

    /// <summary>FEAT-2: sends a test message to one channel (enabled or not) and shows the result.</summary>
    public async Task<IActionResult> OnPostTestAsync(int id, [FromServices] IAlertDispatcher dispatcher,
        [FromServices] TestNotificationLimiter limiter, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var cid = current.RequiredCustomerId;
        var channel = await db.AlertChannels.Where(x => x.Id == id && x.CustomerId == cid).Select(x => new { x.Name, x.Type }).FirstOrDefaultAsync(ct);
        if (channel is null) return NotFound();
        var name = channel.Name;

        if (!limiter.TryAcquire(cid, id, AlertDispatcher.IsSmsOrVoice(channel.Type), out var wait))
        {
            TempData["Error"] = L["Too many test notifications. Try again in {0} seconds.", wait].Value;
            return RedirectToPage("Index");
        }

        var result = await dispatcher.SendTestAsync(cid, id, ct);
        if (result is null) return NotFound();
        if (result.Success)
            TempData["Success"] = L["Test notification sent to \"{0}\".", name].Value;
        else
            TempData["Error"] = L["Test notification to \"{0}\" failed: {1}", name, result.Error ?? L["an unknown error"].Value].Value;
        return RedirectToPage("Index");
    }
}
