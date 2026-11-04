using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Pages.Status;

/// <summary>
/// Target of the subscription confirmation and unsubscribe links. GET only shows a button (mail scanners that prefetch
/// links change nothing); the POST does the work.
/// </summary>
public abstract class SubscriptionLinkModel(AppDbContext db, StatusPageSubscriptions subscriptions) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    public StatusPageRef Ref { get; private set; } = null!;
    public string Base { get; private set; } = "";
    public bool Valid { get; private set; }
    public bool Done { get; private set; }

    protected abstract bool IsConfirm { get; }

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        if (!await LoadAsync(slug, ct)) return NotFound();
        Valid = subscriptions.IsValid(Token, IsConfirm);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug, CancellationToken ct)
    {
        if (!await LoadAsync(slug, ct)) return NotFound();
        Done = IsConfirm ? await subscriptions.ConfirmAsync(Ref.Slug, Token, ct) : await subscriptions.UnsubscribeAsync(Ref.Slug, Token, ct);
        Valid = Done;
        return Page();
    }

    private async Task<bool> LoadAsync(string slug, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var r = await StatusPageRef.FindAsync(db, slug, ct);
        if (r is null) return false;
        Ref = r;
        Base = CustomDomainMiddleware.BasePath(HttpContext, r.Slug);
        return true;
    }
}

[AllowAnonymous]
[EnableRateLimiting(StatusPageSetup.ViewPolicy)]
public class ConfirmModel(AppDbContext db, StatusPageSubscriptions subscriptions) : SubscriptionLinkModel(db, subscriptions)
{
    protected override bool IsConfirm => true;
}
