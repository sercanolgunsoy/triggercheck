using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Web.Api;

namespace TriggerCheck.Web.StatusPages;

/// <summary>
/// Wiring of the public status pages (FEAT-4): services, the background notifier, rate limits, the output cache policy
/// and the anonymous JSON / Atom endpoints. The HTML pages live in Pages/Status.
/// <list type="bullet">
/// <item>Public responses are anonymous (AllowAnonymous: SessionValidator skips them) and set no cookies: the cached
/// page has no form. Only the separate subscribe / confirm / unsubscribe pages carry a POST form with the standard
/// antiforgery token (and therefore its cookie) - a visitor gets that cookie only by deliberately opening them.</item>
/// <item>Page, JSON and feed are served from the output cache for <see cref="StatusPageOptions.CacheSeconds"/>,
/// varied by host, slug and UI language; panel changes evict them (tag <see cref="CacheTag"/>). Signed-in users are
/// never served from / stored in the cache (default policy).</item>
/// <item>Per-client rate limits (<see cref="ViewPolicy"/>, <see cref="SubscribePolicy"/>).</item>
/// </list>
/// </summary>
public static class StatusPageSetup
{
    public const string CachePolicy = "status-page";
    public const string CacheTag = "status-pages";
    public const string ViewPolicy = "status-page-view";
    public const string SubscribePolicy = "status-page-subscribe";
    public const string DomainCheckPolicy = "status-domain-check";
    public const string ReportDownloadPolicy = "report-download";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IServiceCollection AddStatusPages(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StatusPageOptions>(config.GetSection(StatusPageOptions.Section));
        services.AddScoped<PublicStatusService>();
        services.AddScoped<StatusPageSubscriptions>();
        services.AddScoped<StatusPageNotifier>();
        services.AddScoped<CustomDomainReverifier>();
        services.AddSingleton<CustomDomainMap>();
        services.AddSingleton<ICustomDomainVerifier, DnsCustomDomainVerifier>();
        services.AddHostedService<StatusPageNotifierService>();

        var cacheSeconds = config.GetValue($"{StatusPageOptions.Section}:CacheSeconds", 30);
        services.AddOutputCache(o => o.AddPolicy(CachePolicy, b =>
        {
            if (cacheSeconds <= 0) { b.NoCache(); return; }
            b.Expire(TimeSpan.FromSeconds(cacheSeconds))
                .SetVaryByRouteValue("slug")
                // The default policy varies by every query value: arbitrary ?x=... would bypass the cache.
                .SetVaryByQuery(Array.Empty<string>())
                .VaryByValue(_ => new KeyValuePair<string, string>("lang", CultureInfo.CurrentUICulture.Name))
                .Tag(CacheTag);
        }));

        services.AddRateLimiter(o =>
        {
            o.AddPolicy(ViewPolicy, http => PerMinute(http, "status:"));
            // The reverse proxy asks once per new TLS handshake host: loopback (proxy sidecar) is not limited, others get a high budget.
            o.AddPolicy(DomainCheckPolicy, http => http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip)
                ? RateLimitPartition.GetNoLimiter("loopback")
                : RateLimitPartition.GetFixedWindowLimiter("domain-check:" + ApiSetup.ClientPartition(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // E-mailed report links: PDFs are built on demand, so a small budget per client.
            o.AddPolicy(ReportDownloadPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter("report-download:" + ApiSetup.ClientPartition(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(SubscribePolicy, http =>
            {
                if (!HttpMethods.IsPost(http.Request.Method)) return PerMinute(http, "status-sub-get:");
                var limit = http.RequestServices.GetRequiredService<IOptionsMonitor<StatusPageOptions>>().CurrentValue.SubscriptionsPerHourPerClient;
                return RateLimitPartition.GetFixedWindowLimiter("status-sub:" + ApiSetup.ClientPartition(http.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromHours(1), QueueLimit = 0 });
            });
        });
        return services;
    }

    private static RateLimitPartition<string> PerMinute(HttpContext http, string prefix)
    {
        var limit = http.RequestServices.GetRequiredService<IOptionsMonitor<StatusPageOptions>>().CurrentValue.RequestsPerMinutePerClient;
        return RateLimitPartition.GetFixedWindowLimiter(prefix + ApiSetup.ClientPartition(http.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    /// <summary>Custom-domain host rewriting; before routing.</summary>
    public static IApplicationBuilder UseStatusPageCustomDomains(this IApplicationBuilder app) => app.UseMiddleware<CustomDomainMiddleware>();

    /// <summary>Evicts cached public responses (after a panel change).</summary>
    public static async Task EvictAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await services.GetRequiredService<IOutputCacheStore>().EvictByTagAsync(CacheTag, ct);
        services.GetRequiredService<CustomDomainMap>().Invalidate();
    }

    /// <summary>For the reverse proxy's on-demand TLS ("ask" URL): 200 when the host is a servable custom domain, else 404.</summary>
    public const string DomainCheckPath = "/status/domain-check";

    public static void MapStatusPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(DomainCheckPath, async (string? domain, CustomDomainMap map, HttpContext http) =>
        {
            var host = (domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (host.Length is 0 or > StatusPageRules.MaxDomainLength) return Results.NotFound();
            return await map.FindAsync(host, http.RequestAborted) is not null ? Results.Ok() : Results.NotFound();
        }).AllowAnonymous().RequireRateLimiting(DomainCheckPolicy).ExcludeFromDescription();

        app.MapGet("/status/{slug}.json", async (string slug, HttpContext http, PublicStatusService service) =>
        {
            var view = await service.GetAsync(slug, http.RequestAborted);
            if (view is null) return Results.NotFound();
            SetPublicHeaders(http, view);
            return Results.Json(view.Page, Json);
        }).AllowAnonymous().CacheOutput(CachePolicy).RequireRateLimiting(ViewPolicy).ExcludeFromDescription();

        app.MapGet("/status/{slug}/feed", async (string slug, HttpContext http, PublicStatusService service) =>
        {
            var view = await service.GetAsync(slug, http.RequestAborted);
            if (view is null) return Results.NotFound();
            SetPublicHeaders(http, view);
            var basePath = CustomDomainMiddleware.BasePath(http, view.Slug);
            var pageUrl = $"{http.Request.Scheme}://{http.Request.Host}{(basePath.Length == 0 ? "/" : basePath)}";
            return Results.Text(AtomFeed(view, pageUrl), "application/atom+xml; charset=utf-8");
        }).AllowAnonymous().CacheOutput(CachePolicy).RequireRateLimiting(ViewPolicy).ExcludeFromDescription();
    }

    /// <summary>Short public caching; unlisted pages are kept out of search engines.</summary>
    public static void SetPublicHeaders(HttpContext http, StatusPageView view)
    {
        http.Response.Headers.CacheControl = "public, max-age=30";
        if (view.Visibility == StatusPageVisibility.Unlisted) http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }

    /// <summary>Atom feed of the incidents of the last 30 days (XML-escaped by XDocument).</summary>
    public static string AtomFeed(StatusPageView view, string pageUrl)
    {
        XNamespace a = "http://www.w3.org/2005/Atom";
        var p = view.Page;
        static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var updated = p.Incidents.Select(i => i.ResolvedAtUtc ?? i.StartedAtUtc).DefaultIfEmpty(p.GeneratedAtUtc).Max();
        var feed = new XElement(a + "feed",
            new XElement(a + "title", p.Title),
            new XElement(a + "id", $"urn:triggercheck:status:{view.Slug}"),
            new XElement(a + "updated", Iso(updated)),
            new XElement(a + "link", new XAttribute("href", pageUrl)),
            p.Incidents.Select(i => new XElement(a + "entry",
                new XElement(a + "title", $"{i.Monitor}: {(i.ResolvedAtUtc is null ? "ongoing outage" : "resolved outage")}"),
                new XElement(a + "id", $"urn:triggercheck:status:{view.Slug}:{i.StartedAtUtc.Ticks}:{Uri.EscapeDataString(i.Monitor)}"),
                new XElement(a + "updated", Iso(i.ResolvedAtUtc ?? i.StartedAtUtc)),
                new XElement(a + "published", Iso(i.StartedAtUtc)),
                new XElement(a + "link", new XAttribute("href", pageUrl)),
                new XElement(a + "content", new XAttribute("type", "text"),
                    $"Started {Iso(i.StartedAtUtc)}" + (i.ResolvedAtUtc is { } r ? $", resolved {Iso(r)} ({i.DurationMinutes} min)." : ", ongoing.")
                    + string.Concat(i.Notes.Select(n => $"\n\n{Iso(n.CreatedAtUtc)}: {n.Body}"))))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), feed).Declaration + "\n" + feed;
    }
}
