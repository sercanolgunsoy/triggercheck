using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4: public responses are output-cached briefly and evicted by panel changes.</summary>
public class StatusPageCachingTests(CachedStatusPageTestFactory f) : IClassFixture<CachedStatusPageTestFactory>
{
    [Fact]
    public async Task Public_page_is_cached_and_panel_edits_evict_it()
    {
        var (cid, admin, _) = await f.AddTenantAsync("cache");
        var m = await f.AddMonitorAsync(cid, "M");
        var pageId = await f.AddStatusPageAsync(cid, "cache-page", [m]);
        var anon = f.CreateBrowser();
        Assert.Contains("Title cache-page", await anon.GetStringAsync("/status/cache-page"));
        Assert.Contains("Title cache-page", await anon.GetStringAsync("/status/cache-page.json"));

        // A change behind the panel's back is not visible until the entry expires...
        await f.WithDbAsync(db => db.StatusPages.Where(p => p.Id == pageId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Title, "Changed directly")));
        Assert.Contains("Title cache-page", await anon.GetStringAsync("/status/cache-page"));
        Assert.Contains("Title cache-page", await anon.GetStringAsync("/status/cache-page.json"));

        // ...while a panel edit evicts it at once.
        var c = await f.LoginAsync(admin, ReportTestData.Password);
        var r = await c.PostFormAsync($"/App/StatusPages/Edit/{pageId}", new()
        {
            ["Input.Title"] = "Edited in panel", ["Input.Slug"] = "cache-page", ["Input.Visibility"] = "Public", ["Input.TimeZoneId"] = "UTC",
            ["Input.Monitors[0].MonitorId"] = m.ToString(), ["Input.Monitors[0].Selected"] = "true", ["Input.Monitors[0].DisplayName"] = "Web"
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Edited in panel", await anon.GetStringAsync("/status/cache-page"));
        Assert.Contains("Edited in panel", await anon.GetStringAsync("/status/cache-page.json"));

        // The language is part of the cache key.
        var tr = f.CreateBrowser();
        await tr.SetLanguageAsync("tr");
        Assert.Contains("Bildirimlere abone ol", await tr.GetHtmlAsync("/status/cache-page"));
        Assert.Contains("Subscribe to updates", await anon.GetStringAsync("/status/cache-page"));
    }
}

/// <summary>FEAT-4: the cache key ignores the query string; the same slug on the app and on its custom domain stay apart.</summary>
public class StatusPageCacheKeyTests(CachedStatusPageTestFactory f) : IClassFixture<CachedStatusPageTestFactory>
{
    [Fact]
    public async Task Query_variants_hit_the_same_entry_and_are_not_echoed()
    {
        var (cid, _, _) = await f.AddTenantAsync("query");
        var pageId = await f.AddStatusPageAsync(cid, "query-page", [await f.AddMonitorAsync(cid, "M")]);
        var anon = f.CreateBrowser();
        var first = await anon.GetStringAsync("/status/query-page?utm=%3Cscript%3Ex");
        Assert.DoesNotContain("utm", first);
        Assert.DoesNotContain("<script", first);

        await f.WithDbAsync(db => db.StatusPages.Where(p => p.Id == pageId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Title, "Changed directly")));
        foreach (var q in new[] { "", "?a=1", "?utm=other&b=2" })
            Assert.Contains("Title query-page", await anon.GetStringAsync("/status/query-page" + q));
    }

    [Fact]
    public async Task Same_slug_on_app_host_and_custom_domain_are_cached_separately()
    {
        var (cid, _, _) = await f.AddTenantAsync("hosts");
        await f.AddStatusPageAsync(cid, "hosts-page", [await f.AddMonitorAsync(cid, "M")], p =>
        {
            p.CustomDomain = "status.hosts.example"; p.CustomDomainToken = "t"; p.CustomDomainVerifiedAtUtc = f.Now;
        });
        await f.EvictAsync();
        var app = f.CreateBrowser();
        var custom = f.CreateClient(new() { BaseAddress = new Uri("http://status.hosts.example"), AllowAutoRedirect = false });

        Assert.Contains("href=\"/status/hosts-page/subscribe\"", await app.GetStringAsync("/status/hosts-page"));
        var onDomain = await custom.GetStringAsync("/");
        Assert.Contains("href=\"/subscribe\"", onDomain);
        Assert.DoesNotContain("/status/hosts-page", onDomain);
        Assert.Contains("returnUrl=%2F\"", onDomain); // language switch returns to the public path
        Assert.Contains("href=\"/status/hosts-page/subscribe\"", await app.GetStringAsync("/status/hosts-page"));
    }
}

/// <summary>FEAT-4: per-client rate limits of the public endpoints and the subscription form.</summary>
public class StatusPageRateLimitTests(RateLimitedStatusPageTestFactory f) : IClassFixture<RateLimitedStatusPageTestFactory>
{
    [Fact]
    public async Task Public_endpoints_and_subscriptions_are_rate_limited()
    {
        var (cid, _, _) = await f.AddTenantAsync("rl");
        var m = await f.AddMonitorAsync(cid, "M");
        await f.AddStatusPageAsync(cid, "rl-page", [m]);
        var c = f.CreateBrowser();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) statuses.Add((await c.GetAsync("/status/rl-page")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);

        // Subscription POSTs have their own, stricter budget (2 per hour here).
        var posts = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            posts.Add((await c.PostFormAsync("/status/rl-page/subscribe", new() { ["Email"] = $"rl{i}@example.org" })).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], posts);
    }
}
