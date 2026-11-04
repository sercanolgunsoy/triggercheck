using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Api;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    /// <summary>Id of the authenticated API key (also the rate-limit partition).</summary>
    public const string KeyIdClaim = "api_key_id";
    /// <summary>"read" or "write" (write implies read).</summary>
    public const string ScopeClaim = "api_scope";
    public const string ReadScope = "read";
    public const string WriteScope = "write";
}

/// <summary>
/// Authenticates <c>Authorization: Bearer tc_...</c> on /api/v1 only (the Identity cookie forwards /api requests to this
/// scheme, see ApiSetup, so a browser session never authenticates the API and SessionValidator never runs there).
/// Revoked / expired keys and keys of suspended customers are rejected with 401. The principal carries the customer
/// claim used by <see cref="ICurrentCustomer"/>, so all tenant filtering works exactly as in the panel.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);
    private const string FailureKey = "TriggerCheck.ApiKeyFailure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Fail("Use 'Authorization: Bearer <API key>'.");

        var token = header["Bearer ".Length..].Trim();
        if (!ApiKeyTokens.TryGetPrefix(token, out var prefix)) return Fail("The API key is invalid.");

        var key = await db.ApiKeys.AsNoTracking()
            .Where(k => k.Prefix == prefix)
            .Select(k => new { k.Id, k.CustomerId, k.SecretHash, k.Scope, k.ExpiresAtUtc, k.RevokedAtUtc, k.LastUsedAtUtc, CustomerActive = k.Customer.IsActive })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (key is null || !ApiKeyTokens.Matches(token, key.SecretHash)) return Fail("The API key is invalid.");

        // The caller holds the exact key here, so the specific reason can be shown without leaking anything.
        var now = time.GetUtcNow().UtcDateTime;
        if (key.RevokedAtUtc is not null) return Fail("The API key has been revoked.");
        if (key.ExpiresAtUtc is DateTime exp && exp <= now) return Fail("The API key has expired.");
        if (!key.CustomerActive) return Fail("The account is suspended.");

        // Throttled LastUsedAtUtc: at most one write per key and minute (conditional, so concurrent requests don't pile up).
        if (key.LastUsedAtUtc is null || now - key.LastUsedAtUtc.Value >= LastUsedResolution)
        {
            var threshold = now - LastUsedResolution;
            await db.ApiKeys.Where(k => k.Id == key.Id && (k.LastUsedAtUtc == null || k.LastUsedAtUtc <= threshold))
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAtUtc, now), Context.RequestAborted);
        }

        List<Claim> claims =
        [
            new(AppClaims.CustomerId, key.CustomerId.ToString()),
            new(ApiKeyDefaults.KeyIdClaim, key.Id.ToString()),
            new(ApiKeyDefaults.ScopeClaim, ApiKeyDefaults.ReadScope)
        ];
        if (key.Scope == ApiKeyScope.ReadWrite) claims.Add(new(ApiKeyDefaults.ScopeClaim, ApiKeyDefaults.WriteScope));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private AuthenticateResult Fail(string message)
    {
        Context.Items[FailureKey] = message;
        return AuthenticateResult.Fail(message);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = Context.Items[FailureKey] as string;
        Response.Headers.WWWAuthenticate = failure is null ? "Bearer" : "Bearer error=\"invalid_token\"";
        return ApiProblems.Write(Context, StatusCodes.Status401Unauthorized, "Unauthorized",
            failure ?? "An API key is required: send 'Authorization: Bearer <API key>'.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_scope\"";
        return ApiProblems.Write(Context, StatusCodes.Status403Forbidden, "Forbidden", "This API key is read-only. Writes need a read-write key.");
    }
}
