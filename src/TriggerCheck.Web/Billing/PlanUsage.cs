using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.Billing;

/// <summary>One quota of the plan: used / max, with the warning threshold of BIZ-3 (80 %).</summary>
public sealed record UsageMeter(int Used, int Max)
{
    public const int WarningPercent = 80;

    /// <summary>Rounded down, capped at 100 (a customer may be over the limit after a plan change).</summary>
    public int Percent => Max <= 0 ? 100 : (int)Math.Min(100, 100L * Used / Max);
    public bool IsWarning => Percent >= WarningPercent;
    public bool IsFull => Used >= Max;
}

/// <summary>Quotas shown on the dashboard and the billing page.</summary>
/// <param name="AiRequests">Null until the AI layer exposes a quota (see <see cref="IAiQuotaUsageSource"/>).</param>
public sealed record PlanUsage(UsageMeter Monitors, UsageMeter? AiRequests);

/// <summary>
/// EXTENSION POINT for the AI quota (AI-10 / BIZ-3): the AI layer registers an implementation returning the customer's
/// monthly AI usage against its plan budget. Nothing implements it yet, so no AI meter is shown.
/// </summary>
public interface IAiQuotaUsageSource
{
    Task<UsageMeter?> GetAsync(int customerId, CancellationToken ct);
}

public sealed class PlanUsageService(AppDbContext db, IEnumerable<IAiQuotaUsageSource> aiSources)
{
    public async Task<PlanUsage> GetAsync(int customerId, CancellationToken ct = default)
    {
        var max = await db.Customers.Where(c => c.Id == customerId).Select(c => c.Plan.MaxMonitors).FirstAsync(ct);
        var used = await db.Monitors.CountAsync(m => m.CustomerId == customerId, ct);
        UsageMeter? ai = null;
        foreach (var source in aiSources)
            if ((ai = await source.GetAsync(customerId, ct)) is not null) break;
        return new PlanUsage(new UsageMeter(used, max), ai);
    }
}
