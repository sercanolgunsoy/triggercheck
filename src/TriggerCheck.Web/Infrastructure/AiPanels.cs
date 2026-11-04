using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>What an AI-2 / AI-3 block on a page shows (partial "_AiPanel").</summary>
public enum AiPanelState
{
    /// <summary>No AI result exists for this subject (e.g. an incident from before AI was switched on).</summary>
    None,
    /// <summary>A decision rendered with "_AiInterpretation" (succeeded, failed or over budget).</summary>
    Decision,
    /// <summary>Queued or running: the summary appears shortly.</summary>
    Pending,
    /// <summary>Ai:Enabled is off or no key is configured.</summary>
    Off,
    OptOut,
    NotInPlan
}

/// <param name="CanManage">The viewer is a CustomerAdmin (the opt-out note links to the AI settings).</param>
public sealed record AiPanelModel(AiPanelState State, AiContentModel? Content, bool CanManage);

/// <summary>
/// Loads the AI block of one subject (incident, anomaly signal) for the current customer: the latest decision, or why
/// there is none. Every query is filtered by the customer passed in (the page's RequiredCustomerId). Read-only: pages
/// never start an AI call; jobs are queued by the background event watcher.
/// </summary>
public sealed class AiPanels(AppDbContext db, IAiClient client, IOptions<AiOptions> options, TimeProvider time)
{
    /// <summary>How long after its event a subject without a decision is shown as "being prepared".</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(10);

    private static readonly AiDecisionStatus[] Shown =
    [
        AiDecisionStatus.Succeeded, AiDecisionStatus.Failed, AiDecisionStatus.Refused, AiDecisionStatus.Truncated,
        AiDecisionStatus.SkippedBudget, AiDecisionStatus.Pending
    ];

    public bool AiAvailable => options.Value.Enabled && client.IsAvailable;

    /// <param name="customer">The current customer, with its Plan loaded.</param>
    public async Task<AiPanelModel> LoadAsync(Customer customer, string feature, string subjectType, string subjectId, DateTime eventAtUtc,
        string returnUrl, string? userId, bool canManage, CancellationToken ct)
    {
        if (!AiAvailable) return new(AiPanelState.Off, null, canManage);
        if (!customer.AiEnabled) return new(AiPanelState.OptOut, null, canManage);
        if (!AiFeatures.PlanAllows(customer.Plan, feature)) return new(AiPanelState.NotInPlan, null, canManage);

        var latest = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customer.Id && d.Feature == feature && d.SubjectType == subjectType && d.SubjectId == subjectId && Shown.Contains(d.Status))
            .OrderByDescending(d => d.Id)
            .Take(2)
            .ToListAsync(ct);
        // A newer call still running (e.g. the "resolved" summary): keep showing the earlier answer meanwhile.
        var decision = latest.Count > 1 && latest[0].Status == AiDecisionStatus.Pending && latest[1].Status == AiDecisionStatus.Succeeded
            ? latest[1] : latest.FirstOrDefault();
        if (decision is null)
            return new(time.GetUtcNow().UtcDateTime - eventAtUtc < PendingWindow ? AiPanelState.Pending : AiPanelState.None, null, canManage);
        if (decision.Status == AiDecisionStatus.Pending) return new(AiPanelState.Pending, null, canManage);

        int? mine = null;
        if (userId is not null && decision.Status == AiDecisionStatus.Succeeded)
            mine = await db.AiFeedback.AsNoTracking()
                .Where(f => f.DecisionId == decision.Id && f.UserId == userId && f.CustomerId == customer.Id)
                .Select(f => (int?)f.Value).FirstOrDefaultAsync(ct);
        return new(AiPanelState.Decision, AiContentModel.From(decision, returnUrl, mine), canManage);
    }

    /// <summary>
    /// AI-2 interprets only the first incident of a correlated outage (same customer, same host, within
    /// Ai:Events:HostWindowMinutes). For the other incidents this finds that interpreted incident, so the page can show
    /// its summary. Null when there is none.
    /// </summary>
    public async Task<int?> RelatedIncidentAsync(int customerId, int incidentId, CancellationToken ct)
    {
        var inc = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == incidentId && i.Monitor.CustomerId == customerId)
            .Select(i => new { i.StartedAtUtc, i.MonitorId, i.Monitor.Type, i.Monitor.Target })
            .FirstOrDefaultAsync(ct);
        if (inc is null || AiTenantData.HostOf(inc.Type, inc.Target) is not { } host) return null;
        var window = TimeSpan.FromMinutes(Math.Max(0, options.Value.Events.HostWindowMinutes));
        var (from, to) = (inc.StartedAtUtc - window, inc.StartedAtUtc + window);
        var candidates = (await db.Incidents.AsNoTracking()
                .Where(i => i.Monitor.CustomerId == customerId && i.Id != incidentId && i.MonitorId != inc.MonitorId && i.StartedAtUtc >= from && i.StartedAtUtc <= to)
                .Select(i => new { i.Id, i.StartedAtUtc, i.Monitor.Type, i.Monitor.Target })
                .ToListAsync(ct))
            .Where(i => AiTenantData.HostOf(i.Type, i.Target) == host)
            .ToList();
        if (candidates.Count == 0) return null;
        var ids = candidates.Select(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var decided = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customerId && d.Feature == AiFeatures.IncidentInterpretation && d.SubjectType == AiSubjects.Incident
                        && ids.Contains(d.SubjectId) && d.Status == AiDecisionStatus.Succeeded)
            .Select(d => d.SubjectId).Distinct().ToListAsync(ct);
        return candidates.Where(c => decided.Contains(c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .OrderBy(c => c.StartedAtUtc).Select(c => (int?)c.Id).FirstOrDefault();
    }

    /// <summary>Latest succeeded summary per subject id (list pages); empty when AI is unavailable, opted out or not in the plan.</summary>
    public async Task<Dictionary<string, string>> SummariesAsync(Customer customer, string feature, string subjectType, IReadOnlyCollection<string> subjectIds, CancellationToken ct)
    {
        if (!AiAvailable || !customer.AiEnabled || !AiFeatures.PlanAllows(customer.Plan, feature) || subjectIds.Count == 0) return [];
        var rows = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customer.Id && d.Feature == feature && d.SubjectType == subjectType
                        && subjectIds.Contains(d.SubjectId) && d.Status == AiDecisionStatus.Succeeded)
            .Select(d => new { d.Id, d.SubjectId, d.OutputJson })
            .ToListAsync(ct);
        var result = new Dictionary<string, string>();
        foreach (var g in rows.GroupBy(r => r.SubjectId))
            if (AiInterpretation.TryParse(g.OrderByDescending(r => r.Id).First().OutputJson)?.Summary is { Length: > 0 } summary)
                result[g.Key] = summary;
        return result;
    }
}
