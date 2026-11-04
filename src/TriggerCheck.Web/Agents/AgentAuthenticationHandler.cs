using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Areas.App.Support;

namespace TriggerCheck.Web.Agents;

public static class AgentAuthDefaults
{
    public const string Scheme = "ProbeAgent";
    public const string Policy = "ProbeAgent";
    public const string RateLimitPolicy = "AgentPerRegion";
    public const string RegionIdClaim = "agent_region_id";
    public const string RegionCodeClaim = "agent_region";
}

/// <summary>
/// FEAT-6: authenticates probe agents on <see cref="AgentProtocol.BasePath"/> only (a dedicated scheme, used by no other
/// policy): <c>Authorization: Bearer tcr_...</c> plus <see cref="AgentProtocol.RegionHeader"/> naming the region the agent
/// is configured for, which must be the credential's region. Unknown / rotated credentials, disabled regions and
/// mismatching region headers get 401, so disabling a region or rotating its credential stops its leases at the next
/// request. The principal carries no customer claim, so it never passes a panel or customer API policy.
/// </summary>
public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromSeconds(10);
    private const string FailureKey = "TriggerCheck.AgentAuthFailure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Fail("Use 'Authorization: Bearer <agent credential>'.");

        var token = header["Bearer ".Length..].Trim();
        if (!AgentTokens.TryGetPrefix(token, out var prefix)) return Fail("The agent credential is invalid.");
        var region = await db.Regions.AsNoTracking()
            .Where(r => r.Prefix == prefix)
            .Select(r => new { r.Id, r.Code, r.SecretHash, r.IsEnabled, r.LastSeenAtUtc })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (region is null || !AgentTokens.Matches(token, region.SecretHash)) return Fail("The agent credential is invalid.");
        if (!region.IsEnabled) return Fail("The region is disabled.");
        if (!string.Equals(Request.Headers[AgentProtocol.RegionHeader].ToString(), region.Code, StringComparison.Ordinal))
            return Fail("The agent credential does not belong to the region in " + AgentProtocol.RegionHeader + ".");

        var now = time.GetUtcNow().UtcDateTime;
        if (region.LastSeenAtUtc is null || now - region.LastSeenAtUtc.Value >= LastSeenResolution)
        {
            var threshold = now - LastSeenResolution;
            try
            {
                await db.Regions.Where(r => r.Id == region.Id && (r.LastSeenAtUtc == null || r.LastSeenAtUtc <= threshold))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastSeenAtUtc, now), Context.RequestAborted);
            }
            catch (Exception ex) when (MonitorCommands.IsDatabaseBusy(ex))
            {
                Logger.LogDebug("LastSeenAtUtc of region {RegionId} not recorded: database busy", region.Id);
            }
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(AgentAuthDefaults.RegionIdClaim, region.Id.ToString()),
            new Claim(AgentAuthDefaults.RegionCodeClaim, region.Code)
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private static readonly ConcurrentDictionary<string, DateTime> LastWarning = new();
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);
    private const int MaxTrackedClients = 10_000;

    private AuthenticateResult Fail(string message)
    {
        Context.Items[FailureKey] = message;
        // One warning per client (IPv4 / IPv6 /64) and minute; the rest at debug level, so a flood cannot fill the log.
        var client = ApiSetup.ClientPartition(Context.Connection.RemoteIpAddress);
        var now = time.GetUtcNow().UtcDateTime;
        if (LastWarning.Count > MaxTrackedClients) LastWarning.Clear();
        var warn = !LastWarning.TryGetValue(client, out var last) || now - last >= WarningInterval;
        if (warn) LastWarning[client] = now;
        Logger.Log(warn ? LogLevel.Warning : LogLevel.Debug, "Probe agent request rejected from {Client}: {Reason}", client, message);
        return AuthenticateResult.Fail(message);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = Context.Items[FailureKey] as string;
        Response.Headers.WWWAuthenticate = failure is null ? "Bearer" : "Bearer error=\"invalid_token\"";
        return ApiProblems.Write(Context, StatusCodes.Status401Unauthorized, "Unauthorized",
            failure ?? "An agent credential is required: send 'Authorization: Bearer <agent credential>'.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiProblems.Write(Context, StatusCodes.Status403Forbidden, "Forbidden", "This credential cannot use the agent API.");
}
