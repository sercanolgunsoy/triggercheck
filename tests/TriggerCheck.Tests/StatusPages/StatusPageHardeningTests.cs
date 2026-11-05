using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Web.StatusPages;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4 review fixes: host routing, domain claims, notification throttling and concurrency, spam limits.</summary>
public class StatusPageHardeningTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private async Task<int> SubscribeConfirmedAsync(int pageId, string email) =>
        await f.WithDbAsync(async db =>
        {
            var s = new StatusPageSubscriber { StatusPageId = pageId, Email = email, CreatedAtUtc = f.Now, ConfirmedAtUtc = f.Now };
            db.StatusPageSubscribers.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });

    private Task<List<EmailOutbox>> MailsAsync(string to) =>
        f.WithDbAsync(db => db.EmailOutbox.AsNoTracking().Where(m => m.To == to).OrderBy(m => m.Id).ToListAsync());

    private async Task RunNotifierAsync()
    {
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<StatusPageNotifier>().RunAsync(default);
    }

    private HttpClient Host(string host) => f.CreateClient(new() { BaseAddress = new Uri("http://" + host), AllowAutoRedirect = false });

    [Fact]
    public async Task Unknown_hosts_get_404_but_health_stays_reachable()
    {
        var other = Host("unrelated.example");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/status/domain-check?domain=unrelated.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Host("127.0.0.1").GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public async Task Removed_custom_domain_no_longer_reaches_the_app()
    {
        var (cid, admin, _) = await f.AddTenantAsync("removed");
        var m = await f.AddMonitorAsync(cid, "M");
        var pageId = await f.AddStatusPageAsync(cid, "removed-page", [m], p =>
        {
            p.CustomDomain = "status.removed.example"; p.CustomDomainToken = "t"; p.CustomDomainVerifiedAtUtc = f.Now;
        });
        await f.EvictAsync();
        var visitor = Host("status.removed.example");
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/")).StatusCode);

        var c = await f.LoginAsync(admin, Password);
        var r = await c.PostFormAsync($"/App/StatusPages/Domain/{pageId}?handler=Remove", [], $"/App/StatusPages/Domain/{pageId}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public async Task Several_pages_may_claim_a_domain_until_one_verifies_it()
    {
        var (a, adminA, _) = await f.AddTenantAsync("claim-a");
        var (b, adminB, _) = await f.AddTenantAsync("claim-b");
        var pageA = await f.AddStatusPageAsync(a, "claim-a-page", [await f.AddMonitorAsync(a, "M")]);
        var pageB = await f.AddStatusPageAsync(b, "claim-b-page", [await f.AddMonitorAsync(b, "M")]);
        var ca = await f.LoginAsync(adminA, Password);
        var cb = await f.LoginAsync(adminB, Password);
        const string domain = "status.claimed.example";

        Assert.Equal(HttpStatusCode.Redirect, (await ca.PostFormAsync($"/App/StatusPages/Domain/{pageA}?handler=Save", new() { ["Domain"] = domain }, $"/App/StatusPages/Domain/{pageA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await cb.PostFormAsync($"/App/StatusPages/Domain/{pageB}?handler=Save", new() { ["Domain"] = domain }, $"/App/StatusPages/Domain/{pageB}")).StatusCode);
        Assert.Equal(2, await f.WithDbAsync(db => db.StatusPages.CountAsync(p => p.CustomDomain == domain)));
        Assert.Equal(HttpStatusCode.NotFound, (await Host(domain).GetAsync("/")).StatusCode); // nobody verified yet

        f.Verifier.Result = true;
        await cb.PostFormAsync($"/App/StatusPages/Domain/{pageB}?handler=Verify", [], $"/App/StatusPages/Domain/{pageB}");
        var pages = await f.WithDbAsync(db => db.StatusPages.AsNoTracking().Where(p => p.Id == pageA || p.Id == pageB).ToDictionaryAsync(p => p.Id));
        Assert.NotNull(pages[pageB].CustomDomainVerifiedAtUtc);
        Assert.Null(pages[pageA].CustomDomain); // the other claim was dropped
        Assert.Contains("Title claim-b-page", await Host(domain).GetStringAsync("/"));

        var r = await ca.PostFormAsync($"/App/StatusPages/Domain/{pageA}?handler=Save", new() { ["Domain"] = domain }, $"/App/StatusPages/Domain/{pageA}");
        Assert.Contains("This domain is already used by another status page.", await r.ReadHtmlAsync());
    }

    [Fact]
    public async Task Verified_domains_are_rechecked_and_dropped_after_repeated_failures()
    {
        var (cid, _, _) = await f.AddTenantAsync("recheck");
        var pageId = await f.AddStatusPageAsync(cid, "recheck-page", [await f.AddMonitorAsync(cid, "M")], p =>
        {
            p.CustomDomain = "status.recheck.example"; p.CustomDomainToken = "tok"; p.CustomDomainVerifiedAtUtc = f.Now.AddDays(-2);
        });
        await f.EvictAsync();
        f.Verifier.Result = false;

        async Task<StatusPage> RecheckAsync()
        {
            using var scope = f.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CustomDomainReverifier>().RunAsync(default);
            return await f.WithDbAsync(db => db.StatusPages.AsNoTracking().SingleAsync(p => p.Id == pageId));
        }

        var p = await RecheckAsync();
        Assert.Equal(1, p.CustomDomainFailures);
        Assert.NotNull(p.CustomDomainVerifiedAtUtc);
        Assert.Equal(1, (await RecheckAsync()).CustomDomainFailures); // not due again within a day
        f.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(2, (await RecheckAsync()).CustomDomainFailures);
        f.Time.Advance(TimeSpan.FromHours(25));
        p = await RecheckAsync();
        Assert.Null(p.CustomDomainVerifiedAtUtc);
        Assert.Equal(HttpStatusCode.NotFound, (await Host("status.recheck.example").GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Pages_over_the_plan_limit_are_not_served()
    {
        var (cid, _, _) = await f.AddTenantAsync("overlimit", plan: "Basic");
        var m = await f.AddMonitorAsync(cid, "M");
        await f.AddStatusPageAsync(cid, "overlimit-old", [m], p => p.CreatedAtUtc = new DateTime(2025, 1, 1));
        await f.AddStatusPageAsync(cid, "overlimit-new", [m], p => p.CreatedAtUtc = new DateTime(2025, 6, 1));
        Assert.Equal(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync("/status/overlimit-old")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/overlimit-new")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/overlimit-new.json")).StatusCode);
    }

    [Fact]
    public async Task Token_pages_keep_their_strict_referrer_policy()
    {
        var (cid, _, _) = await f.AddTenantAsync("referrer");
        await f.AddStatusPageAsync(cid, "referrer-page", [await f.AddMonitorAsync(cid, "M")]);
        var r = await f.CreateBrowser().GetAsync("/status/referrer-page/confirm?token=x");
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        r = await f.CreateBrowser().GetAsync("/status/referrer-page");
        Assert.Equal("strict-origin-when-cross-origin", r.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Flapping_monitor_gets_one_mail_then_a_digest()
    {
        var (cid, _, _) = await f.AddTenantAsync("flap");
        var m = await f.AddMonitorAsync(cid, "M");
        var page = await f.AddStatusPageAsync(cid, "flap-page", [m]);
        await SubscribeConfirmedAsync(page, "flap@example.org");

        var first = await f.AddIncidentAsync(m, f.Now.AddMinutes(-1), null);
        await RunNotifierAsync();
        Assert.Single(await MailsAsync("flap@example.org"));

        // Recovers and fails again a few minutes later: recorded, not mailed.
        f.Time.Advance(TimeSpan.FromMinutes(3));
        await f.WithDbAsync(db => db.Incidents.Where(i => i.Id == first).ExecuteUpdateAsync(u => u.SetProperty(i => i.ResolvedAtUtc, f.Now.AddMinutes(-2))));
        await f.AddIncidentAsync(m, f.Now.AddMinutes(-1), null);
        await RunNotifierAsync();
        Assert.Single(await MailsAsync("flap@example.org"));

        // After the quiet period: one digest with the current state.
        f.Time.Advance(TimeSpan.FromMinutes(30));
        await RunNotifierAsync();
        await RunNotifierAsync();
        var mails = await MailsAsync("flap@example.org");
        Assert.Equal(2, mails.Count);
        Assert.Contains("Public 0 is down", mails[1].Subject);
        Assert.Contains("changed state 2 times", mails[1].Body);
        Assert.Contains("currently unavailable", mails[1].Body);
    }

    [Fact]
    public async Task Two_notifier_instances_send_each_event_once()
    {
        var (cid, _, _) = await f.AddTenantAsync("twice");
        var m = await f.AddMonitorAsync(cid, "M");
        var page = await f.AddStatusPageAsync(cid, "twice-page", [m]);
        await SubscribeConfirmedAsync(page, "twice@example.org");
        await f.AddIncidentAsync(m, f.Now.AddMinutes(-1), null);
        await f.AddMaintenanceAsync(cid, "Patch", f.Now.AddMinutes(-1), f.Now.AddHours(1), m);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(RunNotifierAsync)));
        var mails = await MailsAsync("twice@example.org");
        Assert.Equal(2, mails.Count);
        Assert.Single(mails, x => x.Subject.Contains("is down"));
        Assert.Single(mails, x => x.Subject.Contains("Maintenance started"));
    }

    [Fact]
    public async Task Confirmation_mails_are_capped_per_address_and_per_page()
    {
        var (cid, _, _) = await f.AddTenantAsync("spam");
        var m = await f.AddMonitorAsync(cid, "M");
        var pages = new List<(int Id, string Slug)>();
        for (var i = 0; i < 4; i++)
            pages.Add((await f.AddStatusPageAsync(cid, $"spam-page-{i}", [m]), $"spam-page-{i}"));

        using var scope = f.Services.CreateScope();
        var subs = scope.ServiceProvider.GetRequiredService<StatusPageSubscriptions>();
        foreach (var (id, slug) in pages)
            await subs.SubscribeAsync(id, slug, "Spam https://evil.example/x and evil.example", "victim@example.org", "https://localhost", default);
        var mails = await MailsAsync("victim@example.org");
        Assert.Equal(3, mails.Count); // per address and day, over all pages
        Assert.All(mails, x => Assert.Equal("Confirm your status page subscription", x.Subject));
        Assert.DoesNotContain("https://evil.example", mails[0].Body);
        Assert.Contains("evil[.]example", mails[0].Body);

        // Per page and hour.
        var strict = ActivatorUtilities.CreateInstance<StatusPageSubscriptions>(scope.ServiceProvider,
            new StaticOptionsMonitor<StatusPageOptions>(new StatusPageOptions { ConfirmationsPerPagePerHour = 2 }));
        for (var i = 0; i < 4; i++)
            await strict.SubscribeAsync(pages[0].Id, pages[0].Slug, "T", $"reader{i}@example.org", "https://localhost", default);
        var sent = await f.WithDbAsync(db => db.EmailOutbox.CountAsync(x => x.To.StartsWith("reader")));
        Assert.Equal(1, sent); // victim@ already used one of the two slots of this page in the last hour
    }

    [Theory]
    [InlineData("Acme status", "Acme status")]
    [InlineData("Visit https://evil.example/login now", "Visit  now")]
    [InlineData("acme.io status", "acme[.]io status")]
    [InlineData("API v1.2", "API v1.2")]
    [InlineData("Line\r\nBreak", "LineBreak")]
    public void Customer_text_in_mails_is_defanged(string input, string expected) => Assert.Equal(expected, StatusPageSubscriptions.Safe(input));
}

/// <summary>Without Site:PublicBaseUrl (Development) unknown hosts and unverified claims pass through unchanged.</summary>
public class NoPublicUrlStatusPageTestFactory : StatusPageTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Site:PublicBaseUrl", "");
    }
}

public class StatusPagePassthroughTests(NoPublicUrlStatusPageTestFactory f) : IClassFixture<NoPublicUrlStatusPageTestFactory>
{
    [Fact]
    public async Task Unverified_claimed_host_passes_through_to_the_app()
    {
        var (cid, _, _) = await f.AddTenantAsync("pass");
        await f.AddStatusPageAsync(cid, "pass-page", [await f.AddMonitorAsync(cid, "M")], p => { p.CustomDomain = "status.pass.example"; p.CustomDomainToken = "t"; });
        await f.EvictAsync();
        var visitor = f.CreateClient(new() { BaseAddress = new Uri("http://status.pass.example"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/Account/Login")).StatusCode);
        Assert.DoesNotContain("Title pass-page", await visitor.GetStringAsync("/"));
    }
}
