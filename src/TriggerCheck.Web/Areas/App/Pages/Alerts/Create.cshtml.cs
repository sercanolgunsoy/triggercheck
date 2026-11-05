using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Alerts;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(AppDbContext db, ICurrentCustomer current, IOptionsMonitor<TwilioOptions> twilio, Text<AppResource> L) : PageModel
{
    [BindProperty] public AlertChannelInput Input { get; set; } = new();
    public IReadOnlyList<AlertChannelType> Types { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => await LoadTypesAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var smsVoice = await LoadTypesAsync(ct);
        Input.Validate(ModelState, L, smsVoice);
        if (!ModelState.IsValid) return Page();

        var c = new AlertChannel { CustomerId = current.RequiredCustomerId };
        Input.ApplyTo(c);
        db.AlertChannels.Add(c);
        await db.SaveChangesAsync(ct);
        TempData["Success"] = L["Alert channel \"{0}\" was added.", c.Name].Value;
        return RedirectToPage("Index");
    }

    private async Task<SmsVoiceAccess> LoadTypesAsync(CancellationToken ct)
    {
        var smsVoice = await AlertChannelInput.SmsVoiceAvailableAsync(db, current.RequiredCustomerId, twilio.CurrentValue, ct);
        Types = AlertChannelInput.AvailableTypes(smsVoice).ToList();
        return smsVoice;
    }
}
