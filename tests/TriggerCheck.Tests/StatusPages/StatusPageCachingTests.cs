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
