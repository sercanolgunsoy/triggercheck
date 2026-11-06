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

/// <summary>
/// Host name → slug of every SERVABLE custom domain: verified, customer active, plan allows custom domains and the page
/// is within the plan's status page limit. One small query, cached for a minute and refreshed after panel changes;
/// bounded by the number of verified domains, so arbitrary Host headers cannot grow it. Unverified claims are not in
/// the map at all.
/// </summary>
public sealed class CustomDomainMap(IServiceScopeFactory scopes, TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IReadOnlyDictionary<string, string>? _map;
    private DateTime _loadedAt;

    public void Invalidate() => _map = null;

    /// <summary>The slug served on <paramref name="host"/>, or null.</summary>
    public async Task<string?> FindAsync(string host, CancellationToken ct)
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
                    var rows = await db.StatusPages.AsNoTracking()
                        .Where(p => p.CustomDomain != null && p.CustomDomainVerifiedAtUtc != null && p.Customer.IsActive
                                    && p.Customer.Plan.AllowsStatusPageCustomDomain)
                        .Select(p => new
                        {
                            Domain = p.CustomDomain!, p.Slug, p.CustomDomainVerifiedAtUtc, p.Customer.Plan.MaxStatusPages,
                            Older = db.StatusPages.Count(o => o.CustomerId == p.CustomerId
                                && (o.CreatedAtUtc < p.CreatedAtUtc || (o.CreatedAtUtc == p.CreatedAtUtc && o.Id < p.Id)))
                        })
                        .ToListAsync(ct);
                    map = rows.Where(r => r.MaxStatusPages is null || r.Older < r.MaxStatusPages)
                        .GroupBy(r => r.Domain, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CustomDomainVerifiedAtUtc).First().Slug, StringComparer.OrdinalIgnoreCase);
                    _loadedAt = time.GetUtcNow().UtcDateTime;
                    _map = map;
                }
            }
            finally { _lock.Release(); }
        }
        return map.TryGetValue(host, out var slug) ? slug : null;
    }
}

/// <summary>
/// Host routing (FEAT-4), before routing:
/// <list type="bullet">
/// <item>A servable custom domain (see <see cref="CustomDomainMap"/>) is rewritten to its page's routes (<c>/</c>,
/// <c>/index.json</c>, <c>/feed</c>, <c>/subscribe</c>, <c>/confirm</c>, <c>/unsubscribe</c>); static assets and the
/// language switch pass; everything else (sign-in, panel, API) answers 404 there.</item>
/// <item>When <c>Site:PublicBaseUrl</c> is set, any other host that is not the app's own answers 404, so a domain that
/// still points here after its claim was removed (or was never verified) never shows the app. Exempt: <c>localhost</c>
/// and IP literals (internal callers), and the paths <c>/health*</c> and <see cref="StatusPageSetup.DomainCheckPath"/>
/// (container probes and the reverse proxy use internal host names).</item>
/// <item>Without <c>Site:PublicBaseUrl</c> (Development, tests) other hosts reach the app unchanged.</item>
/// </list>
/// TLS for custom domains is terminated by the reverse proxy (docs/DEPLOYMENT.md).
/// </summary>
public sealed class CustomDomainMiddleware(RequestDelegate next, CustomDomainMap map, PublicUrls urls)
{
    public const string ItemKey = "TriggerCheck.StatusPageCustomDomain";
    /// <summary>The path the visitor requested before the rewrite (for links back to the same page).</summary>
    public const string OriginalPathKey = "TriggerCheck.StatusPageOriginalPath";
    private static readonly string[] StaticPrefixes = ["/css/", "/lib/", "/js/", "/img/", "/favicon", "/icon", "/apple-touch-icon", "/site.webmanifest"];
    private static readonly string[] PageSuffixes = ["/feed", "/subscribe", "/confirm", "/unsubscribe"];

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
        var appHost = AppHost();
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _) || host == "localhost" || host == appHost)
        {
            await next(context);
            return;
        }
        var path = context.Request.Path.Value ?? "/";
        var slug = await map.FindAsync(host, context.RequestAborted);
        if (slug is null)
        {
            if (appHost is null || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                || path.Equals(StatusPageSetup.DomainCheckPath, StringComparison.OrdinalIgnoreCase))
                await next(context);
            else
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (StaticPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || path.Equals("/set-language", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }
        string? target = path is "" or "/" ? $"/status/{slug}"
            : path.Equals("/index.json", StringComparison.OrdinalIgnoreCase) ? $"/status/{slug}.json"
            : PageSuffixes.FirstOrDefault(s => path.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } suffix ? $"/status/{slug}{suffix}"
            : null;
        if (target is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Items[ItemKey] = true;
        context.Items[OriginalPathKey] = path;
        context.Request.Path = target;
        await next(context);
    }

    private string? AppHost() => Uri.TryCreate(urls.BaseUrl, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    /// <summary>Base path of the page's own links: "" on its custom domain, "/status/{slug}" otherwise.</summary>
    public static string BasePath(HttpContext context, string slug) => context.Items.ContainsKey(ItemKey) ? "" : "/status/" + slug;

    /// <summary>The path the visitor sees (before a custom-domain rewrite).</summary>
    public static string PublicPath(HttpContext context) => context.Items[OriginalPathKey] as string ?? context.Request.Path.Value ?? "/";
}

/// <summary>
/// Periodic re-verification of verified custom domains (the TXT record must stay published): each verified domain is
/// checked again every <see cref="StatusPageOptions.DomainRecheckHours"/>; after
/// <see cref="StatusPageOptions.DomainRecheckFailures"/> consecutive failures it is no longer verified (and no longer
/// served). A few domains per run, oldest check first.
/// </summary>
public sealed class CustomDomainReverifier(AppDbContext db, ICustomDomainVerifier verifier, CustomDomainMap map, TimeProvider time,
    IOptionsMonitor<StatusPageOptions> options, ILogger<CustomDomainReverifier> logger)
{
    public const int BatchSize = 20;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var due = now.AddHours(-Math.Max(1, o.DomainRecheckHours));
        var pages = await db.StatusPages
            .Where(p => p.CustomDomain != null && p.CustomDomainToken != null && p.CustomDomainVerifiedAtUtc != null
                        && (p.CustomDomainCheckedAtUtc ?? p.CustomDomainVerifiedAtUtc) <= due)
            .OrderBy(p => p.CustomDomainCheckedAtUtc ?? p.CustomDomainVerifiedAtUtc).Take(BatchSize).ToListAsync(ct);
        var unverified = 0;
        foreach (var p in pages)
        {
            var ok = await verifier.VerifyAsync(p.CustomDomain!, p.CustomDomainToken!, ct);
            p.CustomDomainCheckedAtUtc = now;
            p.CustomDomainFailures = ok ? 0 : p.CustomDomainFailures + 1;
            if (!ok && p.CustomDomainFailures >= Math.Max(1, o.DomainRecheckFailures))
            {
                p.CustomDomainVerifiedAtUtc = null;
                p.CustomDomainFailures = 0;
                unverified++;
                logger.LogWarning("Custom domain {Domain} of status page {PageId} failed {Count} re-verifications and is no longer served.",
                    p.CustomDomain, p.Id, o.DomainRecheckFailures);
            }
        }
        await db.SaveChangesAsync(ct);
        if (unverified > 0) map.Invalidate();
        return unverified;
    }
}
