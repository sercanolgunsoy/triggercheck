using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.Billing;

/// <summary>
/// Limits free trials (L5): one per normalized address (lower case, "+tag" removed, dots ignored for Gmail) and at most
/// <see cref="BillingOptions.MaxTrialsPerDomain"/> per company domain (public mail providers excepted).
/// </summary>
public static class TrialEligibility
{
    public static string Normalize(string email)
    {
        var e = email.Trim().ToLowerInvariant();
        var at = e.LastIndexOf('@');
        if (at <= 0) return e;
        var local = e[..at];
        var domain = e[(at + 1)..];
        var plus = local.IndexOf('+');
        if (plus > 0) local = local[..plus];
        if (domain is "gmail.com" or "googlemail.com") { local = local.Replace(".", ""); domain = "gmail.com"; }
        return $"{local}@{domain}";
    }

    public static async Task<bool> IsEligibleAsync(AppDbContext db, string trialKey, BillingOptions o, int? exceptCustomerId, CancellationToken ct)
    {
        if (await db.Customers.AnyAsync(c => c.TrialKey == trialKey && c.Id != exceptCustomerId, ct)) return false;
        var domain = trialKey[(trialKey.LastIndexOf('@') + 1)..];
        if (o.MaxTrialsPerDomain <= 0 || o.PublicEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return true;
        var suffix = "@" + domain;
        var used = await db.Customers.CountAsync(c => c.TrialKey != null && c.TrialKey.EndsWith(suffix) && c.Id != exceptCustomerId, ct);
        return used < o.MaxTrialsPerDomain;
    }
}
