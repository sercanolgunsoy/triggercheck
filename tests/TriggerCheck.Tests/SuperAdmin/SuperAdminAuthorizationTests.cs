using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.SuperAdmin;

/// <summary>Customer users must not reach any SuperAdmin page or handler.</summary>
public class SuperAdminAuthorizationTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public SuperAdminAuthorizationTests(TestWebAppFactory f) => _f = f;

    private async Task<string[]> GetUrlsAsync()
    {
        var demo = await _f.DemoCustomerAsync();
        var userId = await _f.WithDbAsync(db => db.Users.Where(u => u.CustomerId == demo.Id).Select(u => u.Id).FirstAsync());
        return
        [
            "/SuperAdmin", "/SuperAdmin/Index",
            "/SuperAdmin/Customers/Index", "/SuperAdmin/Customers/Create",
            $"/SuperAdmin/Customers/Details/{demo.Id}", $"/SuperAdmin/Customers/Edit/{demo.Id}",
            $"/SuperAdmin/Customers/Delete/{demo.Id}", $"/SuperAdmin/Customers/AddUser/{demo.Id}",
            $"/SuperAdmin/Customers/ResetPassword/{demo.Id}/{userId}",
            "/SuperAdmin/Plans/Index", "/SuperAdmin/Plans/Create", $"/SuperAdmin/Plans/Edit/{demo.PlanId}",
            "/SuperAdmin/Monitors/Index"
        ];
    }

    [Fact]
    public async Task Customer_admin_gets_access_denied_on_every_superadmin_page()
    {
        var client = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        foreach (var url in await GetUrlsAsync())
        {
            var r = await client.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"{url} returned {(int)r.StatusCode}");
            Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        var client = _f.CreateBrowser();
        foreach (var url in await GetUrlsAsync())
        {
            var r = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Customer_admin_posts_to_superadmin_handlers_are_rejected_and_change_nothing()
    {
        var demo = await _f.DemoCustomerAsync();
        var monitorId = await _f.WithDbAsync(db => db.Monitors.Where(m => m.CustomerId == demo.Id).Select(m => m.Id).FirstAsync());
        var basicId = await _f.PlanIdAsync("Basic");
        var client = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        // A valid antiforgery token for this user is taken from a page the customer can open (layout logout form).
        const string tokenPage = "/App";

        var posts = new (string Url, Dictionary<string, string> Form)[]
        {
            ($"/SuperAdmin/Customers/Details/{demo.Id}?handler=Suspend", []),
            ($"/SuperAdmin/Customers/Edit/{demo.Id}", new() { ["Input.Name"] = "Hacked", ["Input.ContactEmail"] = "h@h.test", ["Input.PlanId"] = basicId.ToString() }),
            ($"/SuperAdmin/Customers/Delete/{demo.Id}", new() { ["ConfirmName"] = demo.Name }),
            ("/SuperAdmin/Customers/Create", new() { ["Input.Name"] = "Evil", ["Input.ContactEmail"] = "e@e.test", ["Input.PlanId"] = basicId.ToString(),
                ["Input.AdminFullName"] = "E", ["Input.AdminEmail"] = "evil@e.test", ["Input.AdminPassword"] = "Secret123" }),
            ($"/SuperAdmin/Customers/AddUser/{demo.Id}", new() { ["Input.Email"] = "evil2@e.test", ["Input.FullName"] = "E", ["Input.Password"] = "Secret123", ["Input.Role"] = "CustomerAdmin" }),
            ($"/SuperAdmin/Plans/Index?handler=Delete&id={basicId}", []),
            ("/SuperAdmin/Plans/Create", new() { ["Input.Name"] = "Free", ["Input.MinIntervalSeconds"] = "15", ["Input.MaxMonitors"] = "1000", ["Input.RetentionDays"] = "30", ["Input.MonthlyPrice"] = "0" }),
            ($"/SuperAdmin/Monitors/Index?handler=Pause&id={monitorId}", []),
        };

        foreach (var (url, form) in posts)
        {
            var r = await client.PostFormAsync(url, form, tokenPage);
            Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{url} returned {(int)r.StatusCode}");
            if (r.StatusCode == HttpStatusCode.Redirect)
                Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
        }

        await _f.WithDbAsync(async db =>
        {
            var c = await db.Customers.SingleAsync(x => x.Id == demo.Id);
            Assert.True(c.IsActive);
            Assert.Equal("Demo Company", c.Name);
            Assert.Equal(demo.PlanId, c.PlanId);
            Assert.False(await db.Customers.AnyAsync(x => x.Name == "Evil"));
            Assert.False(await db.Users.AnyAsync(u => u.Email == "evil@e.test" || u.Email == "evil2@e.test"));
            Assert.True(await db.Plans.AnyAsync(p => p.Id == basicId));
            Assert.False(await db.Plans.AnyAsync(p => p.Name == "Free"));
            Assert.Equal(MonitorStatus.Unknown, (await db.Monitors.SingleAsync(m => m.Id == monitorId)).Status);
            Assert.True((await db.Monitors.SingleAsync(m => m.Id == monitorId)).IsEnabled);
        });
    }
}
