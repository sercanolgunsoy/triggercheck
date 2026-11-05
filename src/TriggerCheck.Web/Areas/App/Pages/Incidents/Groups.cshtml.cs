using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Incidents;

/// <summary>
/// AI-4: related incidents. A1 proposals ("group these incidents?") that a CustomerAdmin accepts (keeping the members
/// they choose) or rejects, the acceptance rate (decision K3), recent groups (automatic ones can be undone) and the
/// switch to turn grouping off. Viewers see everything read-only. Every query is filtered by RequiredCustomerId.
/// </summary>
public class GroupsModel(AppDbContext db, ICurrentCustomer current, IncidentCorrelationService groups, AiPanels ai, Text<AppResource> L) : PageModel
{
    public const int RecentCount = 20;

    public sealed record MemberRow(int IncidentId, string Label, IncidentGroupRole Role, int MonitorId, string MonitorName,
        DateTime StartedAtUtc, DateTime? ResolvedAtUtc, string? Cause, bool Included, bool Folded, bool? AiRecommends);

    public sealed record GroupRow(IncidentGroup Group, List<MemberRow> Members, AiPanelModel? Ai);

    public Customer Customer { get; private set; } = null!;
    public CorrelationMode Mode { get; private set; }
    public CorrelationAcceptance Acceptance { get; private set; } = null!;
    public List<GroupRow> Proposals { get; private set; } = [];
    public List<GroupRow> Recent { get; private set; } = [];
    public bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);
    public bool PlanAllows => AiFeatures.PlanAllows(Customer.Plan, AiFeatures.CorrelationGrouping);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Customer = await current.GetCustomerAsync(ct);
        var cid = Customer.Id;
        Mode = await groups.ModeAsync(Customer, ct);
        Acceptance = await groups.AcceptanceAsync(cid, ct);

        var proposalIds = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.CustomerId == cid && g.Status == IncidentGroupStatus.Proposed)
            .OrderByDescending(g => g.CreatedAtUtc).Select(g => g.Id).Take(RecentCount).ToListAsync(ct);
        var recentIds = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.CustomerId == cid && g.Status != IncidentGroupStatus.Proposed && g.Status != IncidentGroupStatus.Expired)
            .OrderByDescending(g => g.Status == IncidentGroupStatus.Auto).ThenByDescending(g => g.CreatedAtUtc).Select(g => g.Id).Take(RecentCount).ToListAsync(ct);
        foreach (var id in proposalIds)
            if (await LoadAsync(cid, id, withAi: true, ct) is { } row) Proposals.Add(row);
        foreach (var id in recentIds)
            if (await LoadAsync(cid, id, withAi: false, ct) is { } row) Recent.Add(row);
    }

    private async Task<GroupRow?> LoadAsync(int customerId, int groupId, bool withAi, CancellationToken ct)
    {
        var g = await groups.GetAsync(customerId, groupId, ct);
        if (g is null || g.Members.Count < 2) return null;
        var ordered = CorrelationGroupingProvider.Ordered(g);
        AiPanelModel? panel = null;
        IReadOnlySet<int>? recommended = null;
        if (withAi)
        {
            var subject = g.Id.ToString(CultureInfo.InvariantCulture);
            panel = await ai.LoadAsync(Customer, AiFeatures.CorrelationGrouping, IncidentCorrelationService.SubjectType, subject, g.CreatedAtUtc,
                Url.Page("Groups") ?? "/App/Incidents/Groups", User.FindFirstValue(ClaimTypes.NameIdentifier), IsAdmin, ct);
            if (panel.Content?.Status == AiDecisionStatus.Succeeded)
                recommended = CorrelationPrompts.Recommended(panel.Content.Interpretation, CorrelationGroupingProvider.CandidateKeys(g));
        }
        // Labels match the ones the AI saw ("incident D"), so its explanation can be followed; unlisted members get none.
        var labels = ordered.Select((m, i) => (m.IncidentId, Label: CorrelationPrompts.Label(i))).ToDictionary(x => x.IncidentId, x => x.Label);
        var rows = g.Members
            .OrderBy(m => m.Role).ThenBy(m => m.Incident.StartedAtUtc).ThenBy(m => m.IncidentId)
            .Select(m => new MemberRow(m.IncidentId, labels.GetValueOrDefault(m.IncidentId, ""), m.Role, m.Incident.MonitorId, m.Incident.Monitor.Name,
                m.Incident.StartedAtUtc, m.Incident.ResolvedAtUtc, m.Incident.Cause, m.Included, m.FoldedAtUtc is not null,
                m.Role == IncidentGroupRole.Ambiguous && recommended is not null ? recommended.Contains(m.IncidentId) : null))
            .ToList();
        return new GroupRow(g, rows, panel);
    }

    /// <summary>Accept the proposal with the checked members (unchecked ones keep their own notifications).</summary>
    public async Task<IActionResult> OnPostAcceptAsync(int id, int[]? keep, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var result = await groups.AcceptAsync(current.RequiredCustomerId, id, keep ?? [], UserId, ct);
        return Done(result, L["The incidents were grouped."].Value);
    }

    public async Task<IActionResult> OnPostRejectAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var result = await groups.RejectAsync(current.RequiredCustomerId, id, UserId, ct);
        return Done(result, L["The suggestion was rejected. The incidents stay separate."].Value);
    }

    /// <summary>Undo an automatic group: members still down get their own DOWN notification now.</summary>
    public async Task<IActionResult> OnPostUngroupAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var cid = current.RequiredCustomerId;
        if (!await db.IncidentGroups.AnyAsync(g => g.Id == id && g.CustomerId == cid && g.Status == IncidentGroupStatus.Auto, ct)) return NotFound();
        await groups.DissolveAsync(cid, id, UserId, ct);
        TempData["Success"] = L["The group was undone. Monitors that are still down are notified on their own."].Value;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        await groups.SetEnabledAsync(current.RequiredCustomerId, enabled, UserId, ct);
        TempData["Success"] = (enabled ? L["Grouping of related incidents was switched on."] : L["Grouping of related incidents was switched off. Every incident is notified on its own."]).Value;
        return RedirectToPage();
    }

    private string? UserId => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IActionResult Done(IncidentCorrelationService.DecisionResult result, string success)
    {
        switch (result)
        {
            case IncidentCorrelationService.DecisionResult.NotFound: return NotFound();
            case IncidentCorrelationService.DecisionResult.AlreadyDecided: TempData["Error"] = L["This suggestion was already decided."].Value; break;
            default: TempData["Success"] = success; break;
        }
        return RedirectToPage();
    }

    public string SignalText(string code) => code switch
    {
        "same_host" => L["same host"],
        "same_ip" => L["same IP address"],
        "same_domain" => L["same domain"],
        "same_error_type" => L["same error type"],
        "same_window" => L["started within a few minutes"],
        _ => code
    };

    public string StatusText(IncidentGroupStatus s) => s switch
    {
        IncidentGroupStatus.Proposed => L["Suggested"],
        IncidentGroupStatus.Accepted => L["Accepted"],
        IncidentGroupStatus.Rejected => L["Rejected"],
        IncidentGroupStatus.Expired => L["Expired"],
        IncidentGroupStatus.Auto => L["Grouped automatically"],
        _ => L["Undone"]
    };

    public string RoleText(IncidentGroupRole r) => r switch
    {
        IncidentGroupRole.Primary => L["Primary"],
        IncidentGroupRole.Rule => L["Same signals"],
        _ => L["Possibly related"]
    };
}
