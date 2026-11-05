using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TriggerCheck.Ai.Features.MonitorSetup;
using TriggerCheck.Domain;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Heartbeat;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

[Authorize(Policy = Policies.CustomerAdmin)]
public class CreateModel(MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L, HeartbeatUrlReveal reveal,
    MonitorSetupAssistant assistant) : MonitorFormPageModel
{
    public int CurrentCount { get; private set; }
    public bool QuotaReached => !PlanRules.CanAddMonitor(Plan, CurrentCount);

    /// <summary>AI-9 box: AI on, customer opted in and the plan includes the assistant (hidden otherwise; the form works without it).</summary>
    public bool ShowAssistant { get; private set; }
    public MonitorAssistantView Assistant { get; private set; } = MonitorAssistantView.Empty;

    private async Task LoadAsync(CancellationToken ct)
    {
        var customer = await current.GetCustomerAsync(ct);
        Plan = customer.Plan;
        CurrentCount = await commands.CountAsync(current.RequiredCustomerId, ct);
        ShowAssistant = assistant.IsAvailable && MonitorSetupAssistant.Allowed(customer);
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        SetDefaults();
    }

    private void SetDefaults()
    {
        Input.IntervalSeconds = Plan.MinIntervalSeconds;
        if (!PlanRules.IsIntervalAllowed(Plan, Input.IntervalSeconds)) Input.IntervalSeconds = PlanRules.AllowedIntervals(Plan).FirstOrDefault(300);
        if (Input.TimeoutSeconds >= Input.IntervalSeconds) Input.TimeoutSeconds = Math.Max(1, Input.IntervalSeconds / 2);
    }

    /// <summary>
    /// AI-9 (A1): the assistant proposes values and the page re-renders the form pre-filled and validated like a normal
    /// post (plan rules, SSRF pre-check), so problems show as the usual field errors. Nothing is saved here: the user
    /// reviews the form and saves it with the normal create post (<see cref="OnPostAsync"/>).
    /// </summary>
    public async Task<IActionResult> OnPostAssistAsync(string? assistantRequest, CancellationToken ct)
    {
        await LoadAsync(ct);
        ModelState.Clear(); // the posted box is not the form: render the model values, not posted ones
        Input = new MonitorInput();
        SetDefaults();
        if (!ShowAssistant) return Page();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Forbid();
        var r = await assistant.SuggestAsync(current.RequiredCustomerId, userId, assistantRequest, ct);
        var messages = new List<string>();
        if (r.Status != MonitorSetupStatus.Ready || r.Proposal is null)
        {
            messages.Add(MonitorSetupAssist.StatusMessage(r.Status, L) ?? L["The assistant could not fill in the form this time. Please try again or fill it in yourself."].Value);
            Assistant = new(assistantRequest, null, messages, false, true);
            return Page();
        }

        var p = r.Proposal;
        if (p.Fields is null)
        {
            messages.Add(p.Outcome == MonitorSetupOutcome.Clarify && !string.IsNullOrWhiteSpace(p.Clarification)
                ? p.Clarification
                : L["The assistant could not tell which kind of monitor you need. Please name the address or host and what to check."].Value);
            messages.AddRange(p.Notes.Select(n => MonitorSetupAssist.Describe(n, Plan, L)));
            Assistant = new(assistantRequest, p.Outcome == MonitorSetupOutcome.Clarify ? p.Explanation : null, messages, false, false);
            return Page();
        }

        MonitorSetupAssist.ApplyTo(p.Fields, Input);
        TryValidateModel(Input, nameof(Input)); // data annotations incl. the SSRF pre-check
        Input.Validate(Plan, ModelState, L);    // plan rules and type-specific fields
        messages.AddRange(p.Notes.Select(n => MonitorSetupAssist.Describe(n, Plan, L)));
        if (!ModelState.IsValid) messages.Add(L["Some suggested values need your attention; see the highlighted fields."]);
        messages.Add(L["Review the values and click Create to save. Nothing has been saved yet."]);
        Assistant = new(assistantRequest, p.Explanation, messages, true, !ModelState.IsValid);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);

        if (QuotaReached)
        {
            ModelState.AddModelError("", L["You have reached the monitor limit of your plan ({0}). Upgrade your plan to add a new monitor.", Plan.MaxMonitors]);
            return Page();
        }

        Input.Validate(Plan, ModelState, L);
        if (!ModelState.IsValid) return Page();

        // The quota re-check and the insert run atomically in MonitorCommands (parallel posts cannot exceed the quota).
        var r = await commands.CreateAsync(current.RequiredCustomerId, Plan, Input, ct);
        switch (r.Error)
        {
            case MonitorCommandError.QuotaReached:
                CurrentCount = await commands.CountAsync(current.RequiredCustomerId, ct);
                ModelState.AddModelError("", L["You have reached the monitor limit of your plan ({0}). Upgrade your plan to add a new monitor.", Plan.MaxMonitors]);
                return Page();
            case MonitorCommandError.Busy:
                ModelState.AddModelError("", L["The system is busy right now. Please try again in a moment."]);
                return Page();
        }

        if (r.IssuedHeartbeatToken is { } token)
        {
            // Only the hash is stored: the full URL is shown once on the details page (one-time server-side reveal).
            TempData["Success"] = L["Monitor \"{0}\" was created. Call its ping URL from your job; it goes down when no ping arrives in time.", r.Monitor!.Name].Value;
            var revealId = reveal.Park(User, current.RequiredCustomerId, r.Monitor.Id, HeartbeatEndpoint.PingUrl(Request, token));
            return RedirectToPage("Details", new { id = r.Monitor.Id, reveal = revealId });
        }
        TempData["Success"] = L["Monitor \"{0}\" was created. The first check will run shortly.", r.Monitor!.Name].Value;
        return RedirectToPage("Details", new { id = r.Monitor.Id });
    }
}
