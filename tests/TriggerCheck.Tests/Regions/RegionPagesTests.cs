using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.SuperAdmin;
using TriggerCheck.Web.Agents;

namespace TriggerCheck.Tests.Regions;

/// <summary>FEAT-6 pages: SuperAdmin region management, per-monitor region choice (plan gated) and the details breakdown.</summary>
public class RegionPagesTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private static readonly Regex TokenRx = new("value=\"(tcr_[a-z2-7]{10}_[A-Za-z0-9_-]+)\"");

    [Fact]
    public async Task SuperAdmin_creates_rotates_and_disables_a_region_with_a_one_time_credential()
    {
        var admin = await f.LoginSuperAdminAsync();
        var code = "eu-" + Guid.NewGuid().ToString("N")[..6];
        var r = await admin.PostFormAsync("/SuperAdmin/Regions?handler=Create", new() { ["Input.Code"] = code, ["Input.Name"] = "Frankfurt" }, "/SuperAdmin/Regions");
        var location = r.Headers.Location!.ToString();
        var html = await admin.FollowAsync(r);
        var token = TokenRx.Match(html).Groups[1].Value;
        Assert.StartsWith("tcr_", token);
        var region = await f.WithDbAsync(db => db.Regions.AsNoTracking().SingleAsync(x => x.Code == code));
        Assert.True(AgentTokens.Matches(token, region.SecretHash));
        Assert.DoesNotContain(token, await admin.GetHtmlAsync(location)); // shown once
        Assert.Contains(code, html);

        // Invalid / duplicate / reserved codes.
        foreach (var bad in new[] { code, "central", "Bad Code" })
        {
            var again = await admin.PostFormAsync("/SuperAdmin/Regions?handler=Create", new() { ["Input.Code"] = bad, ["Input.Name"] = "x" }, "/SuperAdmin/Regions");
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
        Assert.Equal(1, await f.WithDbAsync(db => db.Regions.CountAsync(x => x.Code == code)));

        r = await admin.PostFormAsync($"/SuperAdmin/Regions?handler=Rotate&id={region.Id}", new(), "/SuperAdmin/Regions");
        var rotatedToken = TokenRx.Match(await admin.FollowAsync(r)).Groups[1].Value;
        Assert.NotEqual(token, rotatedToken);
        var rotated = await f.WithDbAsync(db => db.Regions.AsNoTracking().SingleAsync(x => x.Id == region.Id));
        Assert.False(AgentTokens.Matches(token, rotated.SecretHash));
        Assert.True(AgentTokens.Matches(rotatedToken, rotated.SecretHash));
        Assert.NotNull(rotated.CredentialRotatedAtUtc);

        r = await admin.PostFormAsync($"/SuperAdmin/Regions?handler=Toggle&id={region.Id}", new(), "/SuperAdmin/Regions");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Regions.Where(x => x.Id == region.Id).Select(x => x.IsEnabled).SingleAsync()));
    }

    [Fact]
    public async Task Plan_form_sets_the_region_limit()
    {
        var admin = await f.LoginSuperAdminAsync();
        var proId = await f.PlanIdAsync("Pro");
        var html = await admin.GetHtmlAsync($"/SuperAdmin/Plans/Edit/{proId}");
        Assert.Contains("name=\"Input.MaxRegions\"", html);
        Assert.Equal(3, await f.WithDbAsync(db => db.Plans.Where(p => p.Id == proId).Select(p => p.MaxRegions).SingleAsync()));
        Assert.Equal(0, await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Enterprise").Select(p => p.MaxRegions).SingleAsync()));
        Assert.Equal(1, await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Standard").Select(p => p.MaxRegions).SingleAsync()));
    }

    [Fact]
    public async Task Customers_cannot_open_the_region_admin_page()
    {
        var customer = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        AppTestData.AssertAccessDenied(await customer.GetAsync("/SuperAdmin/Regions"));
    }

    [Fact]
    public async Task Region_choice_is_limited_by_the_plan()
    {
        var cid = await f.AddCustomerAsync("Regions " + Guid.NewGuid().ToString("N")[..6], "Pro");
        var email = $"admin-{Guid.NewGuid():N}@regions.test";
        await f.AddUserAsync(cid, email, "Passw0rd!", Roles.CustomerAdmin);
        var monitorId = await f.AddMonitorAsync(cid, "Multi");
        var regions = await f.WithDbAsync(async db => new[]
        {
            await RegionTestData.AddRegionAsync(db, "p1-" + cid, DateTime.UtcNow),
            await RegionTestData.AddRegionAsync(db, "p2-" + cid, DateTime.UtcNow),
            await RegionTestData.AddRegionAsync(db, "p3-" + cid, DateTime.UtcNow)
        });
        var client = await f.LoginAsync(email, "Passw0rd!");
        var url = $"/App/Monitors/Regions/{monitorId}";
        Assert.Contains("p1-" + cid, await client.GetHtmlAsync(url));

        // Pro: central + 2 regions.
        var r = await PostAsync(client, url, regions.Select(x => x.Id).ToArray());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("at most 2 additional region(s)", await r.ReadHtmlAsync());
        Assert.Equal(0, await f.WithDbAsync(db => db.MonitorRegionStates.CountAsync(s => s.MonitorId == monitorId)));

        r = await PostAsync(client, url, regions[0].Id, regions[1].Id);
        AppTestData.AssertRedirect(r, "/App/Monitors/Details");
        Assert.Equal(2, await f.WithDbAsync(db => db.MonitorRegionStates.CountAsync(s => s.MonitorId == monitorId)));

        // Details shows the breakdown (central + the two regions).
        var details = await client.GetHtmlAsync($"/App/Monitors/Details/{monitorId}");
        Assert.Contains("data-region=\"central\"", details);
        Assert.Contains($"data-region=\"p1-{cid}\"", details);
        Assert.Contains($"data-region=\"p2-{cid}\"", details);
        Assert.DoesNotContain($"data-region=\"p3-{cid}\"", details);

        // Removing regions again.
        r = await PostAsync(client, url);
        AppTestData.AssertRedirect(r, "/App/Monitors/Details");
        Assert.Equal(0, await f.WithDbAsync(db => db.MonitorRegionStates.CountAsync(s => s.MonitorId == monitorId)));
    }

    [Fact]
    public async Task Plans_without_regions_cannot_add_them_and_other_tenants_monitors_are_not_found()
    {
        var cid = await f.DemoCustomerIdAsync(); // Standard plan: central only
        var monitorId = await f.AddMonitorAsync(cid, "Single region");
        var region = await f.WithDbAsync(db => RegionTestData.AddRegionAsync(db, "s-" + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow));
        var client = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var r = await PostAsync(client, $"/App/Monitors/Regions/{monitorId}", region.Id);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("main region only", await r.ReadHtmlAsync());
        Assert.Equal(0, await f.WithDbAsync(db => db.MonitorRegionStates.CountAsync(s => s.MonitorId == monitorId)));
        Assert.DoesNotContain("Manage regions", await client.GetHtmlAsync($"/App/Monitors/Details/{monitorId}"));

        var other = await f.AddCustomerAsync("Other " + Guid.NewGuid().ToString("N")[..6], "Enterprise");
        var foreign = await f.AddMonitorAsync(other, "Foreign");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/App/Monitors/Regions/{foreign}")).StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_change_regions()
    {
        var cid = await f.DemoCustomerIdAsync();
        var email = $"viewer-{Guid.NewGuid():N}@regions.test";
        await f.AddUserAsync(cid, email, "Passw0rd!", Roles.CustomerViewer);
        var monitorId = await f.AddMonitorAsync(cid, "Viewer region");
        var viewer = await f.LoginAsync(email, "Passw0rd!");
        var r = await viewer.GetAsync($"/App/Monitors/Regions/{monitorId}");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_monitors_have_no_regions()
    {
        var cid = await f.AddCustomerAsync("Hb " + Guid.NewGuid().ToString("N")[..6], "Enterprise");
        var email = $"hb-{Guid.NewGuid():N}@regions.test";
        await f.AddUserAsync(cid, email, "Passw0rd!", Roles.CustomerAdmin);
        var monitorId = await f.AddMonitorAsync(cid, "Cron", m => { m.Type = MonitorType.Heartbeat; m.Target = "/hb/x"; });
        var region = await f.WithDbAsync(db => RegionTestData.AddRegionAsync(db, "h-" + cid, DateTime.UtcNow));
        var client = await f.LoginAsync(email, "Passw0rd!");
        var r = await PostAsync(client, $"/App/Monitors/Regions/{monitorId}", region.Id);
        AppTestData.AssertRedirect(r, "/App/Monitors/Details");
        Assert.Equal(0, await f.WithDbAsync(db => db.MonitorRegionStates.CountAsync(s => s.MonitorId == monitorId)));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, params int[] regionIds)
    {
        var page = await client.GetStringAsync(url);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var fields = regionIds.Select(id => new KeyValuePair<string, string>("RegionIds", id.ToString())).ToList();
        fields.Add(new("__RequestVerificationToken", WebUtility.HtmlDecode(token)));
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }
}
