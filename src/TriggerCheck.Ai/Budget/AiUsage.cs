using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Budget;

/// <summary>
/// AI quota usage for the current calendar month (UTC). Extension point for the plan usage indicators (BIZ-3): the
/// dashboard can show <see cref="AiUsageSnapshot.PercentUsed"/> and warn at <see cref="AiUsageSnapshot.IsNearLimit"/>.
/// Also used by the budget guard before every AI call (AI-10).
/// </summary>
public interface IAiUsage
{
    Task<AiUsageSnapshot> GetMonthlyUsageAsync(int customerId, CancellationToken ct = default);

    /// <summary>Every customer with AI activity or an AI-enabled plan this month (SuperAdmin overview).</summary>
    Task<IReadOnlyList<AiUsageSnapshot>> GetAllMonthlyUsageAsync(CancellationToken ct = default);
}

/// <param name="Calls">Calls actually made this month (succeeded, refused or failed; skips are free).</param>
/// <param name="Tokens">Input (incl. cache reads/writes) + output tokens.</param>
/// <param name="AiAvailable">Global switch on and a key configured.</param>
public sealed record AiUsageSnapshot(
    int CustomerId,
    string CustomerName,
    string PlanName,
    DateTime MonthStartUtc,
    int Calls,
    int CallLimit,
    long Tokens,
    long TokenLimit,
    decimal EstimatedCostUsd,
    bool AiAvailable,
    bool CustomerOptedIn,
    IReadOnlyList<string> PlanFeatures)
{
    public const double WarningPercent = 80;

    /// <summary>Highest of call and token usage, 0-100 (100 when the plan has no AI calls).</summary>
    public double PercentUsed
    {
        get
        {
            if (CallLimit <= 0) return 100;
            var p = 100.0 * Calls / CallLimit;
            if (TokenLimit > 0) p = Math.Max(p, 100.0 * Tokens / TokenLimit);
            return Math.Min(100, p);
        }
    }

    public bool IsOverBudget => CallLimit <= 0 || Calls >= CallLimit || (TokenLimit > 0 && Tokens >= TokenLimit);
    public bool IsNearLimit => PercentUsed >= WarningPercent;
}

public sealed class AiUsageService(AppDbContext db, IAiClient client, IOptions<AiOptions> options, TimeProvider time) : IAiUsage
{
    private static readonly AiDecisionStatus[] CallStatuses = [AiDecisionStatus.Succeeded, AiDecisionStatus.Refused, AiDecisionStatus.Failed];

    public static DateTime MonthStart(DateTime utcNow) => new(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<AiUsageSnapshot> GetMonthlyUsageAsync(int customerId, CancellationToken ct = default) =>
        (await LoadAsync(customerId, ct)).FirstOrDefault()
        ?? throw new InvalidOperationException($"Customer {customerId} not found.");

    public async Task<IReadOnlyList<AiUsageSnapshot>> GetAllMonthlyUsageAsync(CancellationToken ct = default) =>
        (await LoadAsync(null, ct)).Where(s => s.Calls > 0 || s.CallLimit > 0).ToList();

    private async Task<List<AiUsageSnapshot>> LoadAsync(int? customerId, CancellationToken ct)
    {
        var start = MonthStart(time.GetUtcNow().UtcDateTime);
        var customers = await db.Customers.AsNoTracking()
            .Where(c => customerId == null || c.Id == customerId)
            .Select(c => new { c.Id, c.Name, c.AiEnabled, Plan = c.Plan.Name, c.Plan.AiFeatures, c.Plan.AiMonthlyCallLimit, c.Plan.AiMonthlyTokenLimit })
            .ToListAsync(ct);

        // Aggregated in SQL per customer and model; the cost is estimated from the token sums with the model's price.
        var usage = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CreatedAtUtc >= start && CallStatuses.Contains(d.Status) && (customerId == null || d.CustomerId == customerId))
            .GroupBy(d => new { d.CustomerId, d.Model })
            .Select(g => new
            {
                g.Key.CustomerId, g.Key.Model, Calls = g.Count(),
                Input = g.Sum(x => x.InputTokens), Output = g.Sum(x => x.OutputTokens),
                CacheRead = g.Sum(x => x.CacheReadTokens), CacheWrite = g.Sum(x => x.CacheWriteTokens)
            })
            .ToListAsync(ct);

        var o = options.Value;
        var available = o.Enabled && client.IsAvailable;
        return customers.Select(c =>
        {
            var rows = usage.Where(u => u.CustomerId == c.Id).ToList();
            var cost = rows.Sum(r => o.PriceFor(r.Model).Estimate(r.Input, r.Output, r.CacheRead, r.CacheWrite));
            return new AiUsageSnapshot(c.Id, c.Name, c.Plan, start,
                rows.Sum(r => r.Calls), c.AiMonthlyCallLimit,
                rows.Sum(r => r.Input + r.Output + r.CacheRead + r.CacheWrite), c.AiMonthlyTokenLimit,
                decimal.Round(cost, 4), available, c.AiEnabled, Domain.AiFeatures.Parse(c.AiFeatures));
        }).ToList();
    }
}
