using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Ai;

/// <summary>
/// AI status (read-only, from configuration: the kill switch is Ai:Enabled) and per-customer AI usage and estimated
/// cost this month (AI-10). The API key itself is never shown.
/// </summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(AppDbContext db, IAiUsage usage, IAiClient client, IOptions<AiOptions> options) : PageModel
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

    public async Task OnGetAsync(CancellationToken ct)
    {
        KeyConfigured = Options.ResolveApiKey() is not null;
        Rows = (await usage.GetAllMonthlyUsageAsync(ct)).OrderByDescending(r => r.EstimatedCostUsd).ThenByDescending(r => r.Calls).ThenBy(r => r.CustomerName).ToList();

        var start = AiUsageService.MonthStart(DateTime.UtcNow);
        var month = db.AiDecisions.AsNoTracking().Where(d => d.CreatedAtUtc >= start);
        Helpful = await month.CountAsync(d => d.UserFeedback == 1, ct);
        NotHelpful = await month.CountAsync(d => d.UserFeedback == -1, ct);
        ByStatus = (await month.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
    }
}
