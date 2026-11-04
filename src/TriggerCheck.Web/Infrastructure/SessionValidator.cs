using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Makes role / active-state changes effective on the very next request. Pages that change a user's role or
/// active state (or suspend a customer, or reset a password) call UserManager.UpdateSecurityStampAsync; this cookie
/// validator compares the stamp in the cookie with the stored one on every authenticated page request and ends the
/// session when the stamp differs, the user was deleted, the user or its customer is inactive, or the cookie's
/// customer claim no longer matches. Cost: one primary-key lookup of three columns per authenticated page request
/// (a zero ValidationInterval would instead rebuild the whole principal - several queries - and re-issue the cookie on
/// every request). Static assets and /health are skipped. The standard SecurityStampValidator still runs afterwards
/// to refresh the claims periodically.
/// The login page receives a hint: reason=inactive (deactivated / suspended) or reason=changed (stamp rotated).
/// </summary>
public static class SessionValidator
{
    private const string ReasonKey = "TriggerCheck.SessionEndReason";

    /// <summary>Hooks the validator (and the login-page hint) into the Identity application cookie.</summary>
    public static void Attach(CookieAuthenticationOptions o)
    {
        o.Events.OnValidatePrincipal = ValidatePrincipalAsync;
        var redirectToLogin = o.Events.OnRedirectToLogin;
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.HttpContext.Items[ReasonKey] is string reason)
                ctx.RedirectUri = QueryHelpers.AddQueryString(ctx.RedirectUri, "reason", reason);
            return redirectToLogin(ctx);
        };
    }

    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true || IsAnonymousNonPageEndpoint(context.HttpContext)) return;

        var sp = context.HttpContext.RequestServices;
        var claims = sp.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        var userId = context.Principal.FindFirstValue(claims.UserIdClaimType);
        var stamp = context.Principal.FindFirstValue(claims.SecurityStampClaimType);
        var customerClaim = context.Principal.FindFirstValue(AppClaims.CustomerId);

        var row = userId is null ? null : await sp.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.CustomerId, Active = u.IsActive && (u.CustomerId == null || u.Customer!.IsActive) })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        string? reason = row switch
        {
            null => "",
            { Active: false } => "inactive",
            _ when row.SecurityStamp != stamp || row.CustomerId?.ToString() != customerClaim => "changed",
            _ => null
        };
        if (reason is not null)
        {
            if (reason.Length > 0) context.HttpContext.Items[ReasonKey] = reason;
            context.RejectPrincipal();
            await sp.GetRequiredService<SignInManager<ApplicationUser>>().SignOutAsync();
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }

    /// <summary>Static files, /health and similar endpoints: no page, no authorization requirement.</summary>
    private static bool IsAnonymousNonPageEndpoint(HttpContext http)
    {
        var endpoint = http.GetEndpoint();
        return endpoint is not null
            && endpoint.Metadata.GetMetadata<ActionDescriptor>() is null
            && endpoint.Metadata.GetMetadata<IAuthorizeData>() is null;
    }
}
