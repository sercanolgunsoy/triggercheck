using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks.Dns;

namespace TriggerCheck.Web.StatusPages;

/// <summary>Validation and plan rules of status pages (FEAT-4), shared by the panel and the API.</summary>
public static partial class StatusPageRules
{
    public const int MaxTitleLength = 100;
    public const int MinSlugLength = 3;
    public const int MaxSlugLength = 50;
    public const int MaxAnnouncementLength = 2000;
    public const int MaxDisplayNameLength = 100;
    public const int MaxMonitorsPerPage = 100;
    public const int MaxNoteLength = 1000;
    public const int MaxNotesPerIncident = 20;
    public const int MaxDomainLength = 253;
    /// <summary>DNS name (below the custom domain) of the TXT record that proves control of the domain.</summary>
    public const string VerificationLabel = "_triggercheck";
    public const string VerificationPrefix = "triggercheck-verification=";

    /// <summary>Slugs that could be confused with the app's own paths or brand.</summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "about", "account", "domain-check", "admin", "administrator", "api", "app", "assets", "billing", "blog", "css", "dashboard", "docs",
        "edit", "feed", "features", "health", "help", "img", "js", "lib", "login", "logout", "mail", "new", "pricing",
        "register", "root", "security", "settings", "signup", "static", "status", "superadmin", "support", "system",
        "triggercheck", "www"
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex SlugRx();

    public static string NormalizeSlug(string? slug) => (slug ?? "").Trim().ToLowerInvariant();

    /// <summary>Lower-case letters, digits and inner hyphens, 3-50 characters, no "--", not reserved.</summary>
    public static bool IsValidSlug(string slug) =>
        slug.Length is >= MinSlugLength and <= MaxSlugLength && SlugRx().IsMatch(slug) && !slug.Contains("--") && !ReservedSlugs.Contains(slug);

    /// <summary>True when the text has no control characters other than line breaks (plain-text fields).</summary>
    public static bool IsPlainText(string? text, bool multiline) =>
        text is null || !text.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));

    /// <summary>
    /// Lower-case ASCII host name of a custom domain, or null when it is not a usable host name: at least two labels,
    /// letters / digits / hyphens only, no IP literal, not "localhost" and not the app's own host.
    /// </summary>
    public static string? NormalizeDomain(string? value, string? appHost)
    {
        var raw = (value ?? "").Trim().TrimEnd('.');
        if (raw.Length is 0 or > MaxDomainLength || raw.Contains('_') || raw.Contains(':') || raw.Contains('/')) return null;
        string host;
        try { host = DnsMessage.NormalizeName(raw); }
        catch (ArgumentException) { return null; }
        if (!host.Contains('.') || host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return null;
        if (appHost is not null && string.Equals(host, appHost, StringComparison.OrdinalIgnoreCase)) return null;
        return host;
    }

    public static string NewVerificationToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string VerificationRecordName(string domain) => $"{VerificationLabel}.{domain}";

    public static string VerificationRecordValue(string token) => VerificationPrefix + token;

    /// <summary>Plan limit: null = unlimited.</summary>
    public static bool CanAddPage(Plan plan, int currentCount) => plan.MaxStatusPages is not { } max || currentCount < max;

    public static string LimitText(Plan plan) => plan.MaxStatusPages?.ToString() ?? "∞";

    /// <summary>
    /// After a downgrade a customer may have more pages than the plan allows (nothing is deleted): only the oldest
    /// <see cref="Plan.MaxStatusPages"/> pages are served and notified.
    /// </summary>
    public static async Task<bool> IsWithinPlanAsync(AppDbContext db, StatusPage page, CancellationToken ct)
    {
        var max = await db.Customers.Where(c => c.Id == page.CustomerId).Select(c => c.Plan.MaxStatusPages).FirstOrDefaultAsync(ct);
        if (max is null) return true;
        var older = await db.StatusPages.CountAsync(p => p.CustomerId == page.CustomerId
            && (p.CreatedAtUtc < page.CreatedAtUtc || (p.CreatedAtUtc == page.CreatedAtUtc && p.Id < page.Id)), ct);
        return older < max;
    }

    /// <summary>
    /// Adds the page only if the plan limit still allows it, atomically: the row is inserted first and the count taken in
    /// the same transaction (SQLite serializes writers), so two parallel requests cannot both pass the check.
    /// False (nothing saved) when the limit is reached; a DbUpdateException (slug / domain taken) propagates.
    /// </summary>
    public static async Task<bool> AddWithinLimitAsync(AppDbContext db, StatusPage page, Plan plan, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.StatusPages.Add(page);
        await db.SaveChangesAsync(ct);
        if (plan.MaxStatusPages is { } max && await db.StatusPages.CountAsync(p => p.CustomerId == page.CustomerId, ct) > max)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return false;
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The verified page now owns the domain: other (unverified) claims of the same domain are dropped. Only one page per
    /// domain can be verified at a time.
    /// </summary>
    public static Task<int> ReleaseOtherClaimsAsync(AppDbContext db, int pageId, string domain, CancellationToken ct) =>
        db.StatusPages.Where(p => p.CustomDomain == domain && p.Id != pageId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.CustomDomain, (string?)null).SetProperty(p => p.CustomDomainToken, (string?)null)
                .SetProperty(p => p.CustomDomainVerifiedAtUtc, (DateTime?)null).SetProperty(p => p.CustomDomainFailures, 0), ct);
}
