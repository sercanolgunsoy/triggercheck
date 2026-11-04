using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks.Dns;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.StatusPages;

/// <summary>Checks that the customer published the verification TXT record of a custom domain.</summary>
public interface ICustomDomainVerifier
{
    Task<bool> VerifyAsync(string domain, string token, CancellationToken ct);
}

/// <summary>
/// Looks up TXT records of <c>_triggercheck.{domain}</c> through the operator-configured resolvers
/// (Monitoring:DnsResolvers, the same DNS client as the DNS monitors). Only DNS queries are sent; nothing connects to
/// the customer's host.
/// </summary>
public sealed class DnsCustomDomainVerifier(IOptionsMonitor<MonitoringOptions> options, ILogger<DnsCustomDomainVerifier> logger) : ICustomDomainVerifier
{
    public async Task<bool> VerifyAsync(string domain, string token, CancellationToken ct)
    {
        var name = StatusPageRules.VerificationRecordName(domain);
        var expected = StatusPageRules.VerificationRecordValue(token);
        foreach (var resolver in DnsMonitorChecker.ParseResolvers(options.CurrentValue.DnsResolvers, logger))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var answer = await DnsWireClient.QueryAsync(resolver, name, DnsRecordType.TXT, cts.Token);
                if (answer.ResponseCode is 0 or 3)
                    return answer.Records.Any(r => string.Equals(r.Value.Trim().Trim('"'), expected, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or DnsFormatException && !ct.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Custom domain TXT lookup via {Resolver} failed", resolver);
            }
        }
        return false;
    }
}

/// <summary>A custom domain known to the app.</summary>
/// <param name="Servable">Verified, on a plan that allows custom domains, customer active.</param>
public sealed record CustomDomainEntry(string Slug, bool Servable);

/// <summary>
/// Host name → status page map of all custom domains (one small query, cached for a minute and refreshed after
/// panel changes). Bounded by the number of configured domains, so arbitrary Host headers cannot grow it.
/// </summary>
public sealed class CustomDomainMap(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyDictionary<string, CustomDomainEntry>? _map;
    private DateTime _loadedAt;

    public void Invalidate() => _map = null;

    public async Task<CustomDomainEntry?> FindAsync(string host, CancellationToken ct)
    {
        var map = _map;
        if (map is null || time.GetUtcNow().UtcDateTime - _loadedAt > Ttl)
        {
            await _lock.WaitAsync(ct);
            try
            {
                map = _map;
                if (map is null || time.GetUtcNow().UtcDateTime - _loadedAt > Ttl)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    map = (await db.StatusPages.AsNoTracking().Where(p => p.CustomDomain != null)
                            .Select(p => new
                            {
                                Domain = p.CustomDomain!, p.Slug,
                                Servable = p.CustomDomainVerifiedAtUtc != null && p.Customer.IsActive && p.Customer.Plan.AllowsStatusPageCustomDomain
                            })
                            .ToListAsync(ct))
                        .ToDictionary(x => x.Domain, x => new CustomDomainEntry(x.Slug, x.Servable), StringComparer.OrdinalIgnoreCase);
                    _loadedAt = time.GetUtcNow().UtcDateTime;
                    _map = map;
                }
            }
            finally { _lock.Release(); }
        }
        return map.TryGetValue(host, out var entry) ? entry : null;
    }
}

/// <summary>
/// Serves status pages on their verified custom domains (FEAT-4): requests whose Host is a known custom domain are
/// rewritten to the page's routes (<c>/</c>, <c>/index.json</c>, <c>/feed</c>, <c>/subscribe</c>, <c>/confirm</c>,
/// <c>/unsubscribe</c>); static assets pass; everything else (sign-in, panel, API) answers 404 there. Domains that are
/// not (or no longer) verified never serve anything. TLS for these hosts is terminated by the reverse proxy
/// (docs/DEPLOYMENT.md).
/// </summary>
public sealed class CustomDomainMiddleware(RequestDelegate next, CustomDomainMap map, PublicUrls urls)
{
    public const string ItemKey = "TriggerCheck.StatusPageCustomDomain";
    private static readonly string[] StaticPrefixes = ["/css/", "/lib/", "/js/", "/favicon"];
    private static readonly string[] PageSuffixes = ["/feed", "/subscribe", "/confirm", "/unsubscribe"];

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _) || host == "localhost" || host == AppHost())
        {
            await next(context);
            return;
        }
        var entry = await map.FindAsync(host, context.RequestAborted);
        if (entry is null)
        {
            await next(context);
            return;
        }
        if (!entry.Servable)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = context.Request.Path.Value ?? "/";
        if (StaticPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || path.Equals("/set-language", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        string? target = path is "" or "/" ? $"/status/{entry.Slug}"
            : path.Equals("/index.json", StringComparison.OrdinalIgnoreCase) ? $"/status/{entry.Slug}.json"
            : PageSuffixes.FirstOrDefault(s => path.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } suffix ? $"/status/{entry.Slug}{suffix}"
            : null;
        if (target is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Items[ItemKey] = true;
        context.Request.Path = target;
        await next(context);
    }

    private string? AppHost() => Uri.TryCreate(urls.BaseUrl, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    /// <summary>Base path of the page's own links: "" on its custom domain, "/status/{slug}" otherwise.</summary>
    public static string BasePath(HttpContext context, string slug) => context.Items.ContainsKey(ItemKey) ? "" : "/status/" + slug;
}
