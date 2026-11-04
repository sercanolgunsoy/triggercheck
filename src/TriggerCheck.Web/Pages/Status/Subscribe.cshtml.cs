using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Pages.Status;

/// <summary>The status page of a slug (customer active), or null.</summary>
public sealed record StatusPageRef(int Id, string Slug, string Title)
{
    public static Task<StatusPageRef?> FindAsync(AppDbContext db, string? slug, CancellationToken ct)
    {
        var s = StatusPageRules.NormalizeSlug(slug);
        return db.StatusPages.AsNoTracking().Where(p => p.Slug == s && p.Customer.IsActive)
            .Select(p => new StatusPageRef(p.Id, p.Slug, p.Title)).FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// Subscription form of a status page (FEAT-4): double opt-in. The answer is the same whether the address is new,
/// pending or already subscribed (no enumeration). Rate-limited per client; the form uses the standard antiforgery
/// token (this page is not cached and only visitors who open it get the antiforgery cookie).
/// </summary>
[AllowAnonymous]
[EnableRateLimiting(StatusPageSetup.SubscribePolicy)]
public class SubscribeModel(AppDbContext db, StatusPageSubscriptions subscriptions, Text<SharedResource> L) : PageModel
{
    [BindProperty, Display(Name = "E-mail address")]
    public string? Email { get; set; }

    public StatusPageRef Ref { get; private set; } = null!;
    public bool Sent { get; private set; }
    public string Base { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        if (await LoadAsync(slug, ct) is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (await LoadAsync(slug, ct) is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        var email = StatusPageSubscriptions.NormalizeEmail(Email);
        if (email is null)
        {
            ModelState.AddModelError(nameof(Email), L["Enter a valid e-mail address."]);
            return Page();
        }
        await subscriptions.SubscribeAsync(Ref.Id, Ref.Slug, Ref.Title, email, subscriptions.BaseUrl(Request), ct);
        Sent = true;
        return Page();
    }

    private async Task<StatusPageRef?> LoadAsync(string slug, CancellationToken ct)
    {
        var r = await StatusPageRef.FindAsync(db, slug, ct);
        if (r is not null)
        {
            Ref = r;
            Base = CustomDomainMiddleware.BasePath(HttpContext, r.Slug);
        }
        return r;
    }
}
