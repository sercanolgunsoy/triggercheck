using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Escalation;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Escalation;

/// <summary>
/// FEAT-5: the customer's escalation policy. Visible to all members; saving is CustomerAdmin only. Without a policy
/// (or with no steps / disabled) only the normal DOWN alert is sent.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
{
    public class StepInput
    {
        public int? DelayMinutes { get; set; }
        public List<int> ChannelIds { get; set; } = [];
    }

    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public List<StepInput> Steps { get; set; } = [];

    public List<AlertChannel> Channels { get; private set; } = [];
    /// <summary>Saved steps (1-based) none of whose channels is enabled any more: their alert goes to the fallback.</summary>
    public List<int> StepsWithoutChannel { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        await LoadChannelsAsync(cid, ct);
        var policy = await db.EscalationPolicies.AsNoTracking()
            .Where(p => p.CustomerId == cid)
            .Select(p => new
            {
                p.IsEnabled,
                Steps = p.Steps.OrderBy(s => s.DelayMinutes)
                    .Select(s => new StepInput { DelayMinutes = s.DelayMinutes, ChannelIds = s.Channels.Select(c => c.Id).ToList() }).ToList()
            })
            .FirstOrDefaultAsync(ct);
        IsEnabled = policy?.IsEnabled ?? true;
        Steps = policy?.Steps ?? [];
        var enabled = Channels.Where(c => c.IsEnabled).Select(c => c.Id).ToHashSet();
        StepsWithoutChannel = Steps.Select((s, i) => (s, i)).Where(x => !x.s.ChannelIds.Any(enabled.Contains)).Select(x => x.i + 1).ToList();
        Pad();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var cid = current.RequiredCustomerId;
        await LoadChannelsAsync(cid, ct);
        var own = Channels.Select(c => c.Id).ToHashSet();

        var used = Steps.Take(EscalationRunner.MaxSteps).Where(s => s.DelayMinutes is not null || s.ChannelIds.Count > 0).ToList();
        var previous = 0;
        foreach (var (s, i) in used.Select((s, i) => (s, i)))
        {
            if (s.DelayMinutes is not int d || d < 1 || d > EscalationRunner.MaxDelayMinutes)
                ModelState.AddModelError("", L["Step {0}: the delay must be between 1 and {1} minutes.", i + 1, EscalationRunner.MaxDelayMinutes]);
            else if (d <= previous)
                ModelState.AddModelError("", L["Step {0}: each step must come later than the previous one.", i + 1]);
            else
                previous = d;
            s.ChannelIds = s.ChannelIds.Where(own.Contains).Distinct().ToList();
            if (s.ChannelIds.Count == 0)
                ModelState.AddModelError("", L["Step {0}: choose at least one alert channel.", i + 1]);
        }
        if (!ModelState.IsValid)
        {
            Pad();
            return Page();
        }

        // Two admins saving the first policy at once: the loser hits the unique index and retries as an update.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SaveAsync(cid, used, ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
        TempData["Success"] = L["The escalation policy was saved."].Value;
        return RedirectToPage("Index");
    }

    private async Task SaveAsync(int cid, List<StepInput> used, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var policy = await db.EscalationPolicies.Include(p => p.Steps).FirstOrDefaultAsync(p => p.CustomerId == cid, ct);
        if (policy is null) db.EscalationPolicies.Add(policy = new EscalationPolicy { CustomerId = cid, IsEnabled = IsEnabled, EnabledAtUtc = now });
        // Turning the policy on starts the clock now: incidents that are already old are not paged at once.
        if (IsEnabled && !policy.IsEnabled) policy.EnabledAtUtc = now;
        policy.IsEnabled = IsEnabled;
        db.EscalationSteps.RemoveRange(policy.Steps);
        var channels = await db.AlertChannels.Where(c => c.CustomerId == cid).ToListAsync(ct);
        foreach (var s in used)
        {
            var step = new EscalationStep { DelayMinutes = s.DelayMinutes!.Value };
            foreach (var c in channels.Where(c => s.ChannelIds.Contains(c.Id))) step.Channels.Add(c);
            policy.Steps.Add(step);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task LoadChannelsAsync(int cid, CancellationToken ct) =>
        Channels = await db.AlertChannels.AsNoTracking().Where(c => c.CustomerId == cid).OrderBy(c => c.Name).ToListAsync(ct);

    /// <summary>Always offers <see cref="EscalationRunner.MaxSteps"/> rows (empty rows are ignored on save).</summary>
    private void Pad()
    {
        Steps = Steps.Take(EscalationRunner.MaxSteps).ToList();
        while (Steps.Count < EscalationRunner.MaxSteps) Steps.Add(new StepInput());
    }
}
