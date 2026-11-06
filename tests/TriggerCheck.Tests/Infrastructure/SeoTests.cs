using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>Language URLs, head tags, structured data, robots.txt and sitemap.xml of the public site.</summary>
public class SeoTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string Base = "https://localhost"; // Site:PublicBaseUrl of the test host

    private static async Task<AngleSharp.Html.Dom.IHtmlDocument> DocAsync(HttpClient c, string url)
    {
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync());
    }

    private static string? Link(AngleSharp.Html.Dom.IHtmlDocument d, string rel, string? hreflang = null) =>
        d.QuerySelectorAll($"link[rel='{rel}']").FirstOrDefault(l => hreflang is null || l.GetAttribute("hreflang") == hreflang)?.GetAttribute("href");

    private static string? Meta(AngleSharp.Html.Dom.IHtmlDocument d, string attr, string name) =>
        d.QuerySelector($"meta[{attr}='{name}']")?.GetAttribute("content");

    [Theory]
    [InlineData("/", "/", "/tr/")]
    [InlineData("/Features", "/Features", "/tr/Features")]
    [InlineData("/pricing/", "/Pricing", "/tr/Pricing")]
    public async Task English_pages_have_canonical_and_hreflang(string url, string en, string tr)
    {
        var d = await DocAsync(f.CreateBrowser(), url);
        Assert.Equal("en", d.DocumentElement.GetAttribute("lang"));
        Assert.Equal(Base + en, Link(d, "canonical"));
        Assert.Equal(Base + en, Link(d, "alternate", "en"));
        Assert.Equal(Base + tr, Link(d, "alternate", "tr"));
        Assert.Equal(Base + en, Link(d, "alternate", "x-default"));
        Assert.False(string.IsNullOrWhiteSpace(Meta(d, "name", "description")));
        Assert.Equal(Base + "/img/og-image.png", Meta(d, "property", "og:image"));
        Assert.Equal(Base + en, Meta(d, "property", "og:url"));
        Assert.Contains("index", Meta(d, "name", "robots"));
        Assert.DoesNotContain("noindex", Meta(d, "name", "robots"));
    }

    [Theory]
    [InlineData("/tr", "/tr/")]
    [InlineData("/tr/", "/tr/")]
    [InlineData("/tr/Features", "/tr/Features")]
    [InlineData("/TR/pricing", "/tr/Pricing")]
    public async Task Turkish_urls_render_in_Turkish_without_a_cookie(string url, string canonical)
    {
        var d = await DocAsync(f.CreateBrowser(), url);
        Assert.Equal("tr", d.DocumentElement.GetAttribute("lang"));
        Assert.Equal(Base + canonical, Link(d, "canonical"));
        Assert.Equal("tr_TR", Meta(d, "property", "og:locale"));
        // Internal links stay on the Turkish URLs; the language switcher goes to the English URL of the same page.
        Assert.Contains(d.QuerySelectorAll("a[href]"), a => a.GetAttribute("href") == "/tr/Pricing");
        Assert.DoesNotContain(d.QuerySelectorAll("a[href]"), a => a.GetAttribute("href") is "/Pricing" or "/Features");
        var english = d.QuerySelector("a[lang='en']")!.GetAttribute("href")!;
        Assert.Contains("returnUrl=" + Uri.EscapeDataString(canonical == "/tr/" ? "/" : canonical[3..]), english);
    }

    [Fact]
    public async Task Turkish_url_remembers_the_language_for_the_sign_up_page()
    {
        var c = f.CreateBrowser();
        await c.GetAsync("/tr/Pricing");
        Assert.Contains("<html lang=\"tr\">", await c.GetStringAsync("/Account/Register"));
    }

    [Fact]
    public async Task Url_language_beats_the_cookie()
    {
        var c = f.CreateBrowser();
        await c.SetLanguageAsync("en");
        Assert.Equal("tr", (await DocAsync(c, "/tr/Features")).DocumentElement.GetAttribute("lang"));
    }

    [Theory]
    [InlineData("/tr/App")]
    [InlineData("/tr/Account/Login")]
    [InlineData("/tr/nothing")]
    public async Task Only_marketing_pages_have_a_Turkish_url(string url) =>
        Assert.NotEqual(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync(url)).StatusCode);

    [Fact]
    public async Task Pricing_has_valid_product_and_faq_structured_data()
    {
        var d = await DocAsync(f.CreateBrowser(), "/Pricing");
        var blocks = d.QuerySelectorAll("script[type='application/ld+json']").Select(s => JsonDocument.Parse(s.TextContent).RootElement)
            .SelectMany(e => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [e]).ToList();
        var types = blocks.Select(b => b.GetProperty("@type").GetString()).ToList();
        Assert.Contains("Organization", types);
        Assert.Contains("WebSite", types);
        Assert.Contains("FAQPage", types);
        var app = blocks.Single(b => b.GetProperty("@type").GetString() == "SoftwareApplication");
        var offers = app.GetProperty("offers").EnumerateArray().ToList();
        Assert.NotEmpty(offers);
        Assert.All(offers, o => Assert.Equal("USD", o.GetProperty("priceCurrency").GetString()));
        var faq = blocks.Single(b => b.GetProperty("@type").GetString() == "FAQPage").GetProperty("mainEntity").EnumerateArray().ToList();
        // Every question in the structured data is also visible on the page.
        Assert.Equal(d.QuerySelectorAll(".faq summary").Length, faq.Count);
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    public async Task Account_pages_are_noindex(string url)
    {
        var d = await DocAsync(f.CreateBrowser(), url);
        Assert.Contains("noindex", Meta(d, "name", "robots"));
    }

    [Fact]
    public async Task Panel_pages_are_noindex()
    {
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Contains("noindex", Meta(await DocAsync(c, "/App"), "name", "robots"));
    }

    [Fact]
    public async Task Robots_txt_blocks_private_areas_and_points_to_the_sitemap()
    {
        var r = await f.CreateBrowser().GetAsync("/robots.txt");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/plain", r.Content.Headers.ContentType?.MediaType);
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("Disallow: /App\n", text);
        Assert.Contains("Disallow: /SuperAdmin\n", text);
        Assert.Contains("Disallow: /api/\n", text);
        Assert.Contains($"Sitemap: {Base}/sitemap.xml", text);
        Assert.DoesNotContain("Disallow: /\n", text);
    }

    [Fact]
    public async Task Sitemap_lists_both_languages_and_public_status_pages_only()
    {
        await f.WithDbAsync(async db =>
        {
            var cid = await db.Customers.Select(c => c.Id).FirstAsync();
            db.StatusPages.AddRange(
                new StatusPage { CustomerId = cid, Title = "Seo public", Slug = "seo-public" },
                new StatusPage { CustomerId = cid, Title = "Seo unlisted", Slug = "seo-unlisted", Visibility = StatusPageVisibility.Unlisted });
            await db.SaveChangesAsync();
        });

        var r = await f.CreateBrowser().GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var xml = XDocument.Parse(await r.Content.ReadAsStringAsync());
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = xml.Root!.Elements(ns + "url").ToList();
        var locs = urls.Select(u => (string)u.Element(ns + "loc")!).ToList();
        foreach (var path in new[] { "/", "/tr/", "/Features", "/tr/Features", "/Pricing", "/tr/Pricing", "/status/seo-public" })
            Assert.Contains(Base + path, locs);
        Assert.DoesNotContain(Base + "/status/seo-unlisted", locs);
        var features = urls.Single(u => (string)u.Element(ns + "loc")! == Base + "/tr/Features");
        Assert.Equal(
            ["en", "tr", "x-default"],
            features.Elements(xhtml + "link").Select(l => (string)l.Attribute("hreflang")!).Order().ToArray());
    }

    [Theory]
    [InlineData("/icon.svg")]
    [InlineData("/apple-touch-icon.png")]
    [InlineData("/icon-512.png")]
    [InlineData("/site.webmanifest")]
    [InlineData("/img/og-image.png")]
    public async Task Icons_and_share_image_are_served(string url) =>
        Assert.Equal(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync(url)).StatusCode);
}
