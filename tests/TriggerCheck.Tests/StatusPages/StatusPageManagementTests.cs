using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Web.StatusPages;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4: panel pages (validation, tenant isolation, viewer read-only, plan limits, notes, custom domain).</summary>
public class StatusPageManagementTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private static Dictionary<string, string> Form(string title, string slug, params (int Id, bool Selected, string? Name)[] monitors)
    {
        var d = new Dictionary<string, string>
        {
            ["Input.Title"] = title, ["Input.Slug"] = slug, ["Input.Visibility"] = "Public", ["Input.TimeZoneId"] = "UTC",
            ["Input.Announcement"] = "Planned upgrade tonight."
        };
        for (var i = 0; i < monitors.Length; i++)
        {
            d[$"Input.Monitors[{i}].MonitorId"] = monitors[i].Id.ToString();
            d[$"Input.Monitors[{i}].Selected"] = monitors[i].Selected ? "true" : "false";
            d[$"Input.Monitors[{i}].DisplayName"] = monitors[i].Name ?? "";
        }
        return d;
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Admin")]
    [InlineData("api")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("acme.json")]
    [InlineData("acme status")]
    public void Invalid_slugs(string slug) => Assert.False(StatusPageRules.IsValidSlug(StatusPageRules.NormalizeSlug(slug)));

    [Theory]
    [InlineData("acme")]
    [InlineData("Acme-Status-2")]
    public void Valid_slugs(string slug) => Assert.True(StatusPageRules.IsValidSlug(StatusPageRules.NormalizeSlug(slug)));

    [Theory]
    [InlineData("status.example.com", "status.example.com")]
    [InlineData("Status.Example.COM.", "status.example.com")]
    [InlineData("localhost", null)]
    [InlineData("10.0.0.1", null)]
    [InlineData("example", null)]
    [InlineData("https://status.example.com", null)]
    [InlineData("app.triggercheck.test", null)]
    [InlineData("_x.example.com", null)]
    public void Domain_rules(string input, string? expected) =>
        Assert.Equal(expected, StatusPageRules.NormalizeDomain(input, "app.triggercheck.test"));

    [Fact]
    public async Task Admin_creates_edits_and_deletes_a_page()
    {
        var (cid, admin, _) = await f.AddTenantAsync("mgmt");
        var m1 = await f.AddMonitorAsync(cid, "Internal one");
        var m2 = await f.AddMonitorAsync(cid, "Internal two");
        var c = await f.LoginAsync(admin, Password);

        var r = await c.PostFormAsync("/App/StatusPages/Create", Form("Acme status", "Acme-Mgmt", (m2, true, "API"), (m1, true, "Website")));
        AppTestData.AssertRedirect(r, "/App/StatusPages");
        var page = await f.WithDbAsync(db => db.StatusPages.AsNoTracking().Include(p => p.Monitors).SingleAsync(p => p.CustomerId == cid));
        Assert.Equal("acme-mgmt", page.Slug);
        Assert.Equal(["API", "Website"], page.Monitors.OrderBy(m => m.SortOrder).Select(m => m.DisplayName));
        Assert.Contains("Website", await f.CreateBrowser().GetStringAsync("/status/acme-mgmt"));

        var list = await c.GetHtmlAsync("/App/StatusPages");
        Assert.Contains("/status/acme-mgmt", list);

        var edit = await c.GetHtmlAsync($"/App/StatusPages/Edit/{page.Id}");
        Assert.Contains("value=\"Website\"", edit);
        r = await c.PostFormAsync($"/App/StatusPages/Edit/{page.Id}", Form("Acme status 2", "acme-mgmt", (m1, true, "Web"), (m2, false, "API")));
        AppTestData.AssertRedirect(r, "/App/StatusPages");
        page = await f.WithDbAsync(db => db.StatusPages.AsNoTracking().Include(p => p.Monitors).SingleAsync(p => p.Id == page.Id));
        Assert.Equal("Acme status 2", page.Title);
        Assert.Equal(["Web"], page.Monitors.Select(m => m.DisplayName));

        r = await c.PostFormAsync($"/App/StatusPages/Delete/{page.Id}", []);
        AppTestData.AssertRedirect(r, "/App/StatusPages");
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/acme-mgmt")).StatusCode);
    }

    [Fact]
    public async Task Validation_rejects_reserved_taken_slugs_foreign_monitors_and_missing_names()
    {
        var (cid, admin, _) = await f.AddTenantAsync("valid");
        var (other, _, _) = await f.AddTenantAsync("valid-other");
        var own = await f.AddMonitorAsync(cid, "Own");
        var foreign = await f.AddMonitorAsync(other, "Foreign");
        await f.AddStatusPageAsync(other, "taken-slug", [foreign]);
        var c = await f.LoginAsync(admin, Password);

        async Task<string> Post(Dictionary<string, string> form)
        {
            var r = await c.PostFormAsync("/App/StatusPages/Create", form);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return await r.ReadHtmlAsync();
        }
        Assert.Contains("Some words are reserved", await Post(Form("T", "admin", (own, true, "X"))));
        Assert.Contains("This address is already taken.", await Post(Form("T", "taken-slug", (own, true, "X"))));
        Assert.Contains("Unknown monitor.", await Post(Form("T", "valid-1", (foreign, true, "X"))));
        Assert.Contains("needs a public display name", await Post(Form("T", "valid-2", (own, true, ""))));
        Assert.Contains("Select at least one monitor.", await Post(Form("T", "valid-3", (own, false, "X"))));
        Assert.False(await f.WithDbAsync(db => db.StatusPages.AnyAsync(p => p.CustomerId == cid)));
    }

    [Fact]
    public async Task Viewer_is_read_only_and_other_tenants_pages_are_not_found()
    {
        var (cid, _, viewer) = await f.AddTenantAsync("ro");
        var (other, otherAdmin, _) = await f.AddTenantAsync("ro-other");
        var m = await f.AddMonitorAsync(cid, "M");
        var pageId = await f.AddStatusPageAsync(cid, "ro-page", [m]);

        var v = await f.LoginAsync(viewer, Password);
        var html = await v.GetHtmlAsync("/App/StatusPages");
        Assert.Contains("/status/ro-page", html);
        Assert.DoesNotContain("/App/StatusPages/Edit", html);
        AppTestData.AssertAccessDenied(await v.GetAsync("/App/StatusPages/Create"));
        AppTestData.AssertAccessDenied(await v.GetAsync($"/App/StatusPages/Edit/{pageId}"));
        AppTestData.AssertAccessDenied(await v.GetAsync($"/App/StatusPages/Notes/{pageId}"));
        AppTestData.AssertAccessDenied(await v.GetAsync($"/App/StatusPages/Domain/{pageId}"));

        var o = await f.LoginAsync(otherAdmin, Password);
        Assert.DoesNotContain("ro-page", await o.GetHtmlAsync("/App/StatusPages"));
        foreach (var url in new[] { "Edit", "Delete", "Notes", "Domain" })
            Assert.Equal(HttpStatusCode.NotFound, (await o.GetAsync($"/App/StatusPages/{url}/{pageId}")).StatusCode);
        Assert.NotEqual(cid, other);
    }

    [Fact]
    public async Task Plan_limit_is_enforced()
    {
        var (cid, admin, _) = await f.AddTenantAsync("limit", plan: "Basic");
        var m = await f.AddMonitorAsync(cid, "M");
        var c = await f.LoginAsync(admin, Password);
        AppTestData.AssertRedirect(await c.PostFormAsync("/App/StatusPages/Create", Form("One", "limit-one", (m, true, "M"))), "/App/StatusPages");
        var r = await c.GetAsync("/App/StatusPages/Create");
        AppTestData.AssertRedirect(r, "/App/StatusPages");
        Assert.Equal(1, await f.WithDbAsync(db => db.StatusPages.CountAsync(p => p.CustomerId == cid)));
        Assert.DoesNotContain("/App/StatusPages/Create", await c.GetHtmlAsync("/App/StatusPages"));
    }

    [Fact]
    public async Task Notes_are_published_for_page_incidents_only()
    {
        var (cid, admin, _) = await f.AddTenantAsync("notes");
        var shown = await f.AddMonitorAsync(cid, "Shown", m => m.CreatedAtUtc = Utc(2026, 1, 1));
        var private_ = await f.AddMonitorAsync(cid, "Private", m => m.CreatedAtUtc = Utc(2026, 1, 1));
        var incident = await f.AddIncidentAsync(shown, Utc(2026, 4, 1), Utc(2026, 4, 1, 1));
        var privateIncident = await f.AddIncidentAsync(private_, Utc(2026, 4, 1), Utc(2026, 4, 1, 1));
        var pageId = await f.AddStatusPageAsync(cid, "notes-page", [shown]);
        var c = await f.LoginAsync(admin, Password);

        var html = await c.GetHtmlAsync($"/App/StatusPages/Notes/{pageId}");
        Assert.Contains($"data-incident-id=\"{incident}\"", html);
        Assert.DoesNotContain($"data-incident-id=\"{privateIncident}\"", html);

        var r = await c.PostFormAsync($"/App/StatusPages/Notes/{pageId}?handler=Add", new() { ["incidentId"] = incident.ToString(), ["body"] = "Database failover completed." },
            $"/App/StatusPages/Notes/{pageId}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        r = await c.PostFormAsync($"/App/StatusPages/Notes/{pageId}?handler=Add", new() { ["incidentId"] = privateIncident.ToString(), ["body"] = "x" },
            $"/App/StatusPages/Notes/{pageId}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("Database failover completed.", await f.CreateBrowser().GetStringAsync("/status/notes-page"));

        var noteId = await f.WithDbAsync(db => db.StatusPageNotes.Where(n => n.StatusPageId == pageId).Select(n => n.Id).SingleAsync());
        r = await c.PostFormAsync($"/App/StatusPages/Notes/{pageId}?handler=Delete", new() { ["noteId"] = noteId.ToString() }, $"/App/StatusPages/Notes/{pageId}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.DoesNotContain("Database failover completed.", await f.CreateBrowser().GetStringAsync("/status/notes-page"));
    }

    [Fact]
    public async Task Custom_domain_is_verified_by_txt_record_and_only_then_served()
    {
        var (cid, admin, _) = await f.AddTenantAsync("domain");
        var m = await f.AddMonitorAsync(cid, "M");
        var pageId = await f.AddStatusPageAsync(cid, "domain-page", [m]);
        var c = await f.LoginAsync(admin, Password);

        var r = await c.PostFormAsync($"/App/StatusPages/Domain/{pageId}?handler=Save", new() { ["Domain"] = "Status.Domain-Test.example" }, $"/App/StatusPages/Domain/{pageId}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var page = await f.WithDbAsync(db => db.StatusPages.AsNoTracking().SingleAsync(p => p.Id == pageId));
        Assert.Equal("status.domain-test.example", page.CustomDomain);
        Assert.NotNull(page.CustomDomainToken);
        Assert.Contains($"_triggercheck.status.domain-test.example TXT \"triggercheck-verification={page.CustomDomainToken}\"",
            await c.GetHtmlAsync($"/App/StatusPages/Domain/{pageId}"));

        var visitor = f.CreateClient(new() { BaseAddress = new Uri("http://status.domain-test.example"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/")).StatusCode); // not verified
        const string ask = "/status/domain-check?domain=status.domain-test.example";
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync(ask)).StatusCode);

        f.Verifier.Result = false;
        await c.PostFormAsync($"/App/StatusPages/Domain/{pageId}?handler=Verify", [], $"/App/StatusPages/Domain/{pageId}");
        Assert.Null(await f.WithDbAsync(db => db.StatusPages.Where(p => p.Id == pageId).Select(p => p.CustomDomainVerifiedAtUtc).SingleAsync()));

        f.Verifier.Result = true;
        await c.PostFormAsync($"/App/StatusPages/Domain/{pageId}?handler=Verify", [], $"/App/StatusPages/Domain/{pageId}");
        Assert.NotNull(await f.WithDbAsync(db => db.StatusPages.Where(p => p.Id == pageId).Select(p => p.CustomDomainVerifiedAtUtc).SingleAsync()));
        Assert.Contains(("status.domain-test.example", page.CustomDomainToken!), f.Verifier.Calls);

        Assert.Equal(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync(ask)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/domain-check?domain=unknown.example")).StatusCode);
        var home = await visitor.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.ReadHtmlAsync();
        Assert.Contains("Title domain-page", html);
        Assert.Contains("href=\"/subscribe\"", html);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/feed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/css/site.css")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/App")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/v1/monitors")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/status/other-slug")).StatusCode);

        // A plan without custom domains stops serving it.
        await f.WithDbAsync(async db =>
        {
            var basic = await db.Plans.FirstAsync(p => p.Name == "Basic");
            await db.Customers.Where(x => x.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(x => x.PlanId, basic.Id));
        });
        await f.EvictAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/")).StatusCode);
        Assert.Contains("Custom domains are available on the Pro and Enterprise plans.", await c.GetHtmlAsync($"/App/StatusPages/Domain/{pageId}"));
    }

    [Fact]
    public async Task Unknown_hosts_still_reach_the_app()
    {
        var other = f.CreateClient(new() { BaseAddress = new Uri("http://unrelated.example"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/Account/Login")).StatusCode);
    }

    [Fact]
    public async Task Seeded_plans_have_status_page_quotas()
    {
        var plans = await f.WithDbAsync(db => db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Name));
        Assert.Equal(1, plans["Basic"].MaxStatusPages);
        Assert.Equal(3, plans["Standard"].MaxStatusPages);
        Assert.Equal(10, plans["Pro"].MaxStatusPages);
        Assert.Null(plans["Enterprise"].MaxStatusPages);
        Assert.False(plans["Standard"].AllowsStatusPageCustomDomain);
        Assert.True(plans["Pro"].AllowsStatusPageCustomDomain);
        Assert.True(plans["Enterprise"].AllowsStatusPageCustomDomain);
    }

    [Fact]
    public async Task SuperAdmin_edits_status_page_quota()
    {
        var c = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        var plan = await f.WithDbAsync(async db =>
        {
            var p = new Plan { Name = "Status page quota test", MinIntervalSeconds = 60, MaxMonitors = 5, RetentionDays = 30, MonthlyPrice = 15m };
            db.Plans.Add(p);
            await db.SaveChangesAsync();
            return p;
        });
        var html = await c.GetHtmlAsync($"/SuperAdmin/Plans/Edit/{plan.Id}");
        Assert.Contains("Input.MaxStatusPages", html);
        var form = new Dictionary<string, string>
        {
            ["Input.Name"] = plan.Name, ["Input.MinIntervalSeconds"] = plan.MinIntervalSeconds.ToString(), ["Input.MaxMonitors"] = plan.MaxMonitors.ToString(),
            ["Input.RetentionDays"] = plan.RetentionDays.ToString(), ["Input.MonthlyPrice"] = "15", ["Input.IsActive"] = "true",
            ["Input.AllowsSelfSignup"] = "true", ["Input.AiMonthlyCallLimit"] = "0", ["Input.AiMonthlyTokenLimit"] = "0",
            ["Input.MaxStatusPages"] = "", ["Input.AllowsStatusPageCustomDomain"] = "true"
        };
        var r = await c.PostFormAsync($"/SuperAdmin/Plans/Edit/{plan.Id}", form);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var saved = await f.WithDbAsync(db => db.Plans.AsNoTracking().FirstAsync(p => p.Id == plan.Id));
        Assert.Null(saved.MaxStatusPages);
        Assert.True(saved.AllowsStatusPageCustomDomain);
    }
}
