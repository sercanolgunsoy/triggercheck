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

    /// <summary>schema.org SoftwareApplication of the product, with one Offer per active plan (USD, monthly).</summary>
    public static Dictionary<string, object?> SoftwareApplication(HttpContext context, IReadOnlyList<Plan> plans, string description)
    {
        var home = Seo.Absolute(context, "/");
        var pricing = Seo.Absolute(context, Seo.PathFor(Seo.CurrentLanguage, "/Pricing"));
        return new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "SoftwareApplication",
            ["name"] = "TriggerCheck",
            ["url"] = home,
            ["description"] = description,
            ["applicationCategory"] = "BusinessApplication",
            ["applicationSubCategory"] = "Uptime monitoring",
            ["operatingSystem"] = "Web",
            ["inLanguage"] = new[] { "en", "tr" },
            ["image"] = Seo.Absolute(context, "/img/og-image.png"),
            ["publisher"] = new Dictionary<string, object> { ["@id"] = home + "#organization" },
            ["offers"] = plans.Count == 0 ? null : plans.Select(p => new Dictionary<string, object>
            {
                ["@type"] = "Offer",
                ["name"] = p.Name,
                ["price"] = Seo.Price(p.MonthlyPrice),
                ["priceCurrency"] = "USD",
                ["url"] = pricing,
                ["category"] = p.MonthlyPrice == 0 ? "free" : "subscription",
            }).ToArray(),
        };
    }
}

/// <summary>Model of the _PlanCards partial.</summary>
public sealed record PlanCardsModel(IReadOnlyList<Plan> Plans, string ContactEmail);
