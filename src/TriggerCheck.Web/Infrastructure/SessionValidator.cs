using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Makes role / active-state changes effective on the very next request. Pages that change a user's role or
/// active state (or suspend a customer) call UserManager.UpdateSecurityStampAsync; this cookie validator compares the
/// stamp in the cookie with the stored one on EVERY authenticated request and ends the session on a mismatch, or when
/// the user or its customer is inactive. Cost: one primary-key lookup of two columns per authenticated request
/// (a zero ValidationInterval would instead rebuild the whole principal - several queries - and re-issue the cookie
/// on every request). The standard SecurityStampValidator still runs afterwards to refresh the claims periodically.
/// </summary>
public static class SessionValidator
{
    private const string InactiveKey = "TriggerCheck.SessionInactive";

    /// <summary>Hooks the validator (and the "account inactive" login hint) into the Identity application cookie.</summary>
    public static void Attach(CookieAuthenticationOptions o)
    {
        o.Events.OnValidatePrincipal = ValidatePrincipalAsync;
        var redirectToLogin = o.Events.OnRedirectToLogin;
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.HttpContext.Items.ContainsKey(InactiveKey))
                ctx.RedirectUri = QueryHelpers.AddQueryString(ctx.RedirectUri, "reason", "inactive");
            return redirectToLogin(ctx);
        };
    }

    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true) return;

        var sp = context.HttpContext.RequestServices;
        var claims = sp.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var userId = context.Principal.FindFirstValue(claims.UserIdClaimType);
        var stamp = context.Principal.FindFirstValue(claims.SecurityStampClaimType);

        var row = userId is null ? null : await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, Active = u.IsActive && (u.CustomerId == null || u.Customer!.IsActive) })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (row is null || row.SecurityStamp != stamp || !row.Active)
        {
            if (row is { Active: false }) context.HttpContext.Items[InactiveKey] = true;
            context.RejectPrincipal();
            await sp.GetRequiredService<SignInManager<ApplicationUser>>().SignOutAsync();
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }
}
