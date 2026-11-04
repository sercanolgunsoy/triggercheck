using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

/// <summary>
/// Custom domain of a status page (Pro / Enterprise plans). The domain is served only after the customer proved control
/// with a DNS TXT record; changing it resets the verification. Several pages may claim a domain until one verifies it
/// (a verified domain cannot be claimed again); the verification drops the other claims.
/// </summary>
[Authorize(Policy = Policies.CustomerAdmin)]
public class DomainModel(AppDbContext db, ICurrentCustomer current, ICustomDomainVerifier verifier, PublicUrls urls, TimeProvider time,
    Text<AppResource> L) : PageModel
{
    [BindProperty] public string? Domain { get; set; }
    public StatusPage StatusPage { get; private set; } = null!;
    public bool PlanAllows { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        Domain = StatusPage.CustomDomain;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (!PlanAllows) return Page();
        var appHost = Uri.TryCreate(urls.BaseUrl, UriKind.Absolute, out var u) ? u.Host : null;
        var domain = StatusPageRules.NormalizeDomain(Domain, appHost);
        if (domain is null)
            ModelState.AddModelError(nameof(Domain), L["Enter a host name such as status.example.com."]);
        else if (await db.StatusPages.AnyAsync(p => p.CustomDomain == domain && p.Id != id && p.CustomDomainVerifiedAtUtc != null, ct))
            ModelState.AddModelError(nameof(Domain), L["This domain is already used by another status page."]);
        if (!ModelState.IsValid) return Page();

        if (StatusPage.CustomDomain != domain)
        {
            StatusPage.CustomDomain = domain;
            StatusPage.CustomDomainToken = StatusPageRules.NewVerificationToken();
            StatusPage.CustomDomainVerifiedAtUtc = null;
            StatusPage.CustomDomainCheckedAtUtc = null;
            StatusPage.CustomDomainFailures = 0;
            await db.SaveChangesAsync(ct);
            await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        }
        TempData["Success"] = L["Domain saved. Publish the TXT record below, then verify."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostVerifyAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        if (!PlanAllows || StatusPage.CustomDomain is null || StatusPage.CustomDomainToken is null) return RedirectToPage(new { id });
        if (await verifier.VerifyAsync(StatusPage.CustomDomain, StatusPage.CustomDomainToken, ct))
        {
            // Whoever proves control of the domain owns it; other (unverified or stale) claims are dropped.
            StatusPage.CustomDomainVerifiedAtUtc = time.GetUtcNow().UtcDateTime;
            StatusPage.CustomDomainCheckedAtUtc = StatusPage.CustomDomainVerifiedAtUtc;
            StatusPage.CustomDomainFailures = 0;
            await db.SaveChangesAsync(ct);
            await StatusPageRules.ReleaseOtherClaimsAsync(db, id, StatusPage.CustomDomain, ct);
            await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
            TempData["Success"] = L["The domain {0} is verified.", StatusPage.CustomDomain].Value;
        }
        else
            TempData["Error"] = L["The TXT record was not found yet. DNS changes can take a while; try again later."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        StatusPage.CustomDomain = null;
        StatusPage.CustomDomainToken = null;
        StatusPage.CustomDomainVerifiedAtUtc = null;
        await db.SaveChangesAsync(ct);
        await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        TempData["Success"] = L["The custom domain was removed."].Value;
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await db.StatusPages.FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return false;
        StatusPage = page;
        PlanAllows = (await current.GetCustomerAsync(ct)).Plan.AllowsStatusPageCustomDomain;
        return true;
    }
}
