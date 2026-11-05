using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Ai;

/// <summary>
/// AI status (read-only, from configuration: the kill switch is Ai:Enabled) and per-customer AI usage and estimated
/// cost this month (AI-10). The API key itself is never shown.
/// </summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db, IAiUsage usage, IAiClient client, IOptions<AiOptions> options, TimeProvider time, Text<AdminResource> L,
    IncidentCorrelationService correlation, TriggerCheck.Ai.Insights.InsightSummaryService insightSummaries) : PageModel
{
    public AiOptions Options => options.Value;
    public bool KeyConfigured { get; private set; }
    public bool ClientAvailable => client.IsAvailable;
    public IReadOnlyList<AiUsageSnapshot> Rows { get; private set; } = [];
    public decimal TotalCost => Rows.Sum(r => r.EstimatedCostUsd);
    public int TotalCalls => Rows.Sum(r => r.Calls);
    public int Helpful { get; private set; }
    public int NotHelpful { get; private set; }
    public Dictionary<AiDecisionStatus, int> ByStatus { get; private set; } = new();
    /// <summary>AI-4: platform-wide acceptance of grouping suggestions (decision K3: A3 only above 90 % over four weeks).</summary>
    public CorrelationAcceptance GroupingAcceptance { get; private set; } = null!;
    /// <summary>AI-12: insight summaries are billed to the platform budget (no customer), shown separately.</summary>
    public TriggerCheck.Ai.Insights.InsightPlatformUsage PlatformInsights { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct)
    {
        KeyConfigured = Options.ResolveApiKey() is not null;
        Rows = (await usage.GetAllMonthlyUsageAsync(ct)).OrderByDescending(r => r.EstimatedCostUsd).ThenByDescending(r => r.Calls).ThenBy(r => r.CustomerName).ToList();

        var start = AiUsageService.MonthStart(time.GetUtcNow().UtcDateTime);
        var month = db.AiDecisions.AsNoTracking().Where(d => d.CreatedAtUtc >= start);
        var votes = db.AiFeedback.AsNoTracking().Where(f => f.AtUtc >= start);
        Helpful = await votes.CountAsync(f => f.Value == 1, ct);
        NotHelpful = await votes.CountAsync(f => f.Value == -1, ct);
        ByStatus = (await month.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        GroupingAcceptance = await correlation.AcceptanceAsync(null, ct);
        PlatformInsights = await insightSummaries.GetUsageAsync(ct);
    }

    public string StatusName(AiDecisionStatus s) => s switch
    {
        AiDecisionStatus.Succeeded => L["Succeeded"],
        AiDecisionStatus.Refused => L["Refused by the model"],
        AiDecisionStatus.Failed => L["Failed"],
        AiDecisionStatus.Truncated => L["Truncated (output limit)"],
        AiDecisionStatus.Pending => L["In progress"],
        AiDecisionStatus.SkippedBudget => L["Skipped: budget used up"],
        AiDecisionStatus.SkippedOptOut => L["Skipped: customer opted out"],
        AiDecisionStatus.SkippedPlan => L["Skipped: not in plan"],
        AiDecisionStatus.SkippedDisabled => L["Skipped: AI off"],
        _ => s.ToString()
    };
}
