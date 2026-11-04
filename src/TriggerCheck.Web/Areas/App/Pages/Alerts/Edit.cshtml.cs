using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class EditModel(AppDbContext db, ICurrentCustomer current, IOptionsMonitor<TwilioOptions> twilio, Text<AppResource> L) : PageModel
{
    [BindProperty] public AlertChannelInput Input { get; set; } = new();
    public int Id { get; private set; }
    public IReadOnlyList<AlertChannelType> Types { get; private set; } = [];
    /// <summary>A Telegram bot token is stored: the token field may stay empty to keep it.</summary>
    public bool HasStoredSecret { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        Id = id;
        Input = AlertChannelInput.From(c);
        await LoadAsync(c, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var c = await db.AlertChannels.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
        if (c is null) return NotFound();
        Id = id;
        var smsVoice = await LoadAsync(c, ct);

        Input.Validate(ModelState, L, smsVoice, hasStoredSecret: HasStoredSecret);
        if (!ModelState.IsValid) return Page();

        Input.ApplyTo(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Alert channel \"{0}\" was updated.", c.Name].Value;
        return RedirectToPage("Index");
    }

    private async Task<bool> LoadAsync(AlertChannel c, CancellationToken ct)
    {
        HasStoredSecret = c.Type == AlertChannelType.Telegram && !string.IsNullOrEmpty(c.Secret);
        var smsVoice = await AlertChannelInput.SmsVoiceAvailableAsync(db, current.RequiredCustomerId, twilio.CurrentValue, ct);
        Types = AlertChannelInput.AvailableTypes(smsVoice, c.Type).ToList();
        return smsVoice;
    }
}
