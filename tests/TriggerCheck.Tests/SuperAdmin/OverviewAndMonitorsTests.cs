using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.SuperAdmin;

public class OverviewAndMonitorsTests
{
    [Fact]
    public async Task All_superadmin_pages_render_for_superadmin()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var userId = await f.WithDbAsync(db => db.Users.Where(u => u.CustomerId == demo.Id).Select(u => u.Id).FirstAsync());
        var sa = await f.LoginSuperAdminAsync();
        var tr = await f.LoginSuperAdminAsync();
        await tr.SetLanguageAsync("tr");
        foreach (var client in new[] { sa, tr })
        foreach (var url in new[]
        {
            "/SuperAdmin", "/SuperAdmin/Index", "/SuperAdmin/Customers/Index", "/SuperAdmin/Customers/Create",
            $"/SuperAdmin/Customers/Details/{demo.Id}", $"/SuperAdmin/Customers/Edit/{demo.Id}", $"/SuperAdmin/Customers/Delete/{demo.Id}",
            $"/SuperAdmin/Customers/AddUser/{demo.Id}", $"/SuperAdmin/Customers/ResetPassword/{demo.Id}/{userId}",
            "/SuperAdmin/Plans/Index", "/SuperAdmin/Plans/Create", $"/SuperAdmin/Plans/Edit/{demo.PlanId}", "/SuperAdmin/Monitors/Index"
        })
        {
            var r = await client.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)r.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.NotFound, (await sa.GetAsync("/SuperAdmin/Customers/Details/9999")).StatusCode);
    }

    [Fact]
    public async Task Overview_shows_platform_numbers_incidents_and_scheduler_health()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        await f.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var m = await db.Monitors.FirstAsync(x => x.CustomerId == demo.Id);
            m.Status = MonitorStatus.Down;
            m.NextCheckAtUtc = now.AddMinutes(-10); // 60s interval, 10 min late -> overdue
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddMinutes(-5), IsUp = false, ResponseTimeMs = 10 });
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddHours(-3), IsUp = true, ResponseTimeMs = 10 });
            db.CheckResults.Add(new CheckResult { MonitorId = m.Id, CheckedAtUtc = now.AddDays(-3), IsUp = true, ResponseTimeMs = 10 });
            db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = now.AddMinutes(-5), Cause = "HTTP 503 hata-kodu" });
            db.Customers.Add(new Customer { Name = "Askıdaki", ContactEmail = "a@a.test", PlanId = demo.PlanId, IsActive = false });
            await db.SaveChangesAsync();
        });
        var sa = await f.LoginSuperAdminAsync();
        var html = WebUtility.HtmlDecode(await sa.GetStringAsync("/SuperAdmin/Index"));

        Assert.Contains("1 active", html);
        Assert.Contains("1 suspended", html);
        Assert.Contains("DOWN 1", html);
        Assert.Contains("HTTP 503 hata-kodu", html);
        Assert.Contains("Overdue monitors: 1", html);
        Assert.Matches(new Regex(@"last 24 hours: <strong>2</strong>"), html);
        Assert.Contains("$15.00", html); // MRR: only the active Standard customer counts
    }

    [Fact]
    public async Task Overview_scheduler_ok_when_nothing_overdue()
    {
        using var f = new TestWebAppFactory();
        var sa = await f.LoginSuperAdminAsync();
        var html = await sa.GetStringAsync("/SuperAdmin/Index");
        Assert.Contains("id=\"scheduler-ok\"", html);
    }

    [Fact]
    public async Task Monitors_list_filters_pages_and_pauses_resumes()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        await f.AddMonitorsAsync(demo.Id, 60, 300, "Bulk");
        var sa = await f.LoginSuperAdminAsync();

        static int Rows(string html) => Regex.Matches(html, "handler=(Pause|Resume)").Count;

        var page1 = await sa.GetStringAsync("/SuperAdmin/Monitors/Index");
        Assert.Equal(50, Rows(page1));
        Assert.Contains("1–50 / 61", WebUtility.HtmlDecode(page1));
        var page2 = await sa.GetStringAsync("/SuperAdmin/Monitors/Index?p=2");
        Assert.Equal(11, Rows(page2));

        var search = await sa.GetStringAsync("/SuperAdmin/Monitors/Index?Q=example.com");
        Assert.Equal(1, Rows(search));

        var monitorId = await f.WithDbAsync(db => db.Monitors.Where(m => m.Name == "Example.com").Select(m => m.Id).SingleAsync());
        var r = await sa.PostFormAsync($"/SuperAdmin/Monitors/Index?handler=Pause&id={monitorId}", [], "/SuperAdmin/Monitors/Index");
        Assert.Contains("\"Example.com\" was paused.", await sa.FollowAsync(r));
        var m = await f.WithDbAsync(db => db.Monitors.SingleAsync(x => x.Id == monitorId));
        Assert.False(m.IsEnabled);
        Assert.Equal(MonitorStatus.Paused, m.Status);

        var paused = await sa.GetStringAsync("/SuperAdmin/Monitors/Index?Status=Paused");
        Assert.Equal(1, Rows(paused));
        Assert.Equal(0, Rows(await sa.GetStringAsync("/SuperAdmin/Monitors/Index?Type=Tcp")));
        Assert.Equal(61, Rows(await sa.GetStringAsync($"/SuperAdmin/Monitors/Index?CustomerId={demo.Id}&Type=Http&p=1")) + Rows(await sa.GetStringAsync($"/SuperAdmin/Monitors/Index?CustomerId={demo.Id}&Type=Http&p=2")));

        r = await sa.PostFormAsync($"/SuperAdmin/Monitors/Index?handler=Resume&id={monitorId}", [], "/SuperAdmin/Monitors/Index");
        Assert.Contains("\"Example.com\" was resumed.", await sa.FollowAsync(r));
        m = await f.WithDbAsync(db => db.Monitors.SingleAsync(x => x.Id == monitorId));
        Assert.True(m.IsEnabled);
        Assert.Equal(MonitorStatus.Unknown, m.Status);
        Assert.NotNull(m.NextCheckAtUtc);
    }

    [Fact]
    public async Task Superadmin_pause_closes_open_incident_like_the_customer_panel()
    {
        using var f = new TestWebAppFactory();
        var demo = await f.DemoCustomerAsync();
        var monitorId = await f.WithDbAsync(async db =>
        {
            var m = await db.Monitors.FirstAsync(x => x.CustomerId == demo.Id);
            m.Status = MonitorStatus.Down;
            db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
            return m.Id;
        });
        var sa = await f.LoginSuperAdminAsync();

        await sa.PostFormAsync($"/SuperAdmin/Monitors/Index?handler=Pause&id={monitorId}", [], "/SuperAdmin/Monitors/Index");

        var m = await f.WithDbAsync(db => db.Monitors.SingleAsync(x => x.Id == monitorId));
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.True(m.DownWhenPaused);
        Assert.False(await f.WithDbAsync(db => db.Incidents.AnyAsync(i => i.MonitorId == monitorId && i.ResolvedAtUtc == null)));
    }
}
