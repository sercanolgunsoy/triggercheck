using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Search engine support for the public site.
/// <list type="bullet">
/// <item>Language in the URL: the marketing pages (<see cref="Pages"/>) are served in English at their own path and in
/// Turkish under <c>/tr</c> (<c>/tr/</c>, <c>/tr/Features</c>, <c>/tr/Pricing</c>), so crawlers, which send no culture
/// cookie, can index both languages. The prefix wins over the cookie; unprefixed paths keep using the cookie.</item>
/// <item>Canonical and hreflang links, Open Graph / Twitter tags and JSON-LD (rendered by <c>_SeoHead</c>).</item>
/// <item><c>/robots.txt</c> and <c>/sitemap.xml</c> (marketing pages in both languages plus public status pages).</item>
/// </list>
/// The panel, SuperAdmin and account pages are marked noindex in their layouts.
/// </summary>
public static class Seo
{
    public const string TurkishPrefix = "/tr";
    /// <summary>HttpContext item: the UI language taken from the URL prefix.</summary>
    public const string UrlLanguageKey = "TriggerCheck.UrlLanguage";

    /// <summary>Indexable marketing pages, by canonical path.</summary>
    public static readonly string[] Pages = ["/", "/Features", "/Pricing"];

    /// <summary>The canonical spelling of a marketing page path (case and trailing slash ignored), or null.</summary>
    public static string? MatchPage(string? path)
    {
        var p = string.IsNullOrEmpty(path) ? "/" : path.Length > 1 ? path.TrimEnd('/') : path;
        if (p.Length == 0) p = "/";
        return Pages.FirstOrDefault(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Path of a marketing page in a language: "/Features" → "/tr/Features", "/" → "/tr/".</summary>
    public static string PathFor(string lang, string page) =>
        lang == LocalizationSetup.Turkish ? (page == "/" ? TurkishPrefix + "/" : TurkishPrefix + page) : page;

    public static string CurrentLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == LocalizationSetup.Turkish
        ? LocalizationSetup.Turkish : LocalizationSetup.English;

    /// <summary>
    /// A link to a marketing page (optionally with a #fragment) in the current UI language, so a Turkish visitor or
    /// crawler stays on the Turkish URLs. Other paths are returned unchanged.
    /// </summary>
    public static string Href(string pathAndFragment)
    {
        var hash = pathAndFragment.IndexOf('#');
        var path = hash < 0 ? pathAndFragment : pathAndFragment[..hash];
        var fragment = hash < 0 ? "" : pathAndFragment[hash..];
        return MatchPage(path) is { } page ? PathFor(CurrentLanguage, page) + fragment : pathAndFragment;
    }

    /// <summary>The marketing page being rendered (after the /tr rewrite), or null for any other page.</summary>
    public static string? CurrentPage(HttpContext context) => MatchPage(context.Request.Path.Value);

    /// <summary>Strips the /tr prefix from marketing page requests and records the language for the culture provider.</summary>
    public static IApplicationBuilder UseUrlLanguage(this IApplicationBuilder app) => app.Use((context, next) =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments(TurkishPrefix, StringComparison.OrdinalIgnoreCase, out var rest)
            && MatchPage(rest.Value) is { } page)
        {
            context.Items[UrlLanguageKey] = LocalizationSetup.Turkish;
            context.Request.Path = page;
            // Keep a visitor who arrived on a Turkish URL (e.g. from search) in Turkish on the sign-up and sign-in pages.
            if (LocalizationSetup.CookieLanguage(context.Request) != LocalizationSetup.Turkish)
                LocalizationSetup.AppendLanguageCookie(context.Response, LocalizationSetup.Turkish);
        }
        return next(context);
    });

    // ---- head tags -----------------------------------------------------------------------------------------

    public static string Absolute(HttpContext context, string path) =>
        context.RequestServices.GetRequiredService<PublicUrls>().Build(context.Request, path);

    /// <summary>
    /// JSON-LD script body. Non-ASCII letters are written as-is; &lt;, &gt;, &amp; and quotes are still escaped, so the
    /// content cannot close the tag. Rendered without the CSP nonce (&lt;!script&gt;): a data block is never executed.
    /// </summary>
    public static IHtmlContent JsonLd(object data) => new HtmlString(JsonSerializer.Serialize(data, JsonLdOptions));

    private static readonly JsonSerializerOptions JsonLdOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Locale(string lang) => lang == LocalizationSetup.Turkish ? "tr_TR" : "en_US";

    public static string Price(decimal price) => price.ToString("0.00", CultureInfo.InvariantCulture);

    // ---- robots.txt / sitemap.xml --------------------------------------------------------------------------

    /// <summary>Paths crawlers have no business in (noindex pages stay crawlable so the noindex is seen).</summary>
    private static readonly string[] Disallowed =
        ["/App", "/SuperAdmin", "/api/", "/hb/", "/billing/", "/reports/", "/incidents/", "/set-language", "/health"];

    public static void MapSeoEndpoints(this WebApplication app)
    {
        app.MapGet("/robots.txt", (HttpContext ctx) =>
        {
            var sb = new StringBuilder("User-agent: *\n");
            foreach (var d in Disallowed) sb.Append("Disallow: ").Append(d).Append('\n');
            sb.Append("Allow: /\n\nSitemap: ").Append(Absolute(ctx, "/sitemap.xml")).Append('\n');
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Text(sb.ToString(), "text/plain; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/sitemap.xml", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            // Public status pages on their app address; pages with a verified custom domain are canonical there.
            var slugs = await db.StatusPages.AsNoTracking()
                .Where(p => p.Visibility == StatusPageVisibility.Public && p.CustomDomainVerifiedAtUtc == null)
                .OrderBy(p => p.Slug).Select(p => p.Slug).ToListAsync(ct);
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(BuildSitemap(path => Absolute(ctx, path), slugs), "application/xml; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();
    }

    public static string BuildSitemap(Func<string, string> absolute, IEnumerable<string> statusSlugs)
    {
        const string ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        const string xhtml = "http://www.w3.org/1999/xhtml";
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("urlset", ns);
            w.WriteAttributeString("xmlns", "xhtml", null, xhtml);
            foreach (var page in Pages)
            foreach (var lang in LocalizationSetup.Languages)
            {
                w.WriteStartElement("url", ns);
                w.WriteElementString("loc", ns, absolute(PathFor(lang, page)));
                w.WriteElementString("changefreq", ns, "weekly");
                w.WriteElementString("priority", ns, page == "/" ? "1.0" : "0.8");
                foreach (var alt in LocalizationSetup.Languages) WriteAlternate(w, xhtml, alt, absolute(PathFor(alt, page)));
                WriteAlternate(w, xhtml, "x-default", absolute(page));
                w.WriteEndElement();
            }
            foreach (var slug in statusSlugs)
            {
                w.WriteStartElement("url", ns);
                w.WriteElementString("loc", ns, absolute("/status/" + Uri.EscapeDataString(slug)));
                w.WriteElementString("changefreq", ns, "daily");
                w.WriteElementString("priority", ns, "0.3");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        // StringBuilder output is UTF-16 internally; the declaration must match the bytes we send.
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
    }

    private static void WriteAlternate(XmlWriter w, string xhtml, string lang, string href)
    {
        w.WriteStartElement("xhtml", "link", xhtml);
        w.WriteAttributeString("rel", "alternate");
        w.WriteAttributeString("hreflang", lang);
        w.WriteAttributeString("href", href);
        w.WriteEndElement();
    }
}
