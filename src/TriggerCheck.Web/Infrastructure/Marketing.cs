using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>Data shared by the public pages (home, features, pricing).</summary>
public static class Marketing
{
    /// <summary>Sales / support address for "Request access" links (<c>Site:ContactEmail</c>).</summary>
    public static string ContactEmail(IConfiguration config) => config["Site:ContactEmail"] is { Length: > 0 } e ? e : "hello@triggercheck.local";

    public static string MailTo(IConfiguration config, string subject) =>
        $"mailto:{ContactEmail(config)}?subject={Uri.EscapeDataString(subject)}";

    /// <summary>Plans a visitor can buy, cheapest first.</summary>
    public static async Task<List<Plan>> ActivePlansAsync(AppDbContext db, CancellationToken ct = default)
    {
        // SQLite cannot ORDER BY decimal, so sort in memory (there are only a handful of plans).
        var plans = await db.Plans.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        return plans.OrderBy(p => p.MonthlyPrice).ThenByDescending(p => p.MinIntervalSeconds).ToList();
    }

    /// <summary>Index of the plan shown as "Recommended": the middle one when there are three or more.</summary>
    public static int FeaturedIndex(int count) => count >= 3 ? count / 2 : -1;
}

/// <summary>Model of the _PlanCards partial.</summary>
public sealed record PlanCardsModel(IReadOnlyList<Plan> Plans, string ContactEmail);
