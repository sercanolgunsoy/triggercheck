using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

/// <summary>Customer A (demo) must never see or touch customer B's data, even when guessing ids.</summary>
public class TenantIsolationTests : IClassFixture<TenantIsolationTests.Fixture>
{
    public class Fixture : TestWebAppFactory
    {
        public int OtherCustomerId;
        public int OtherMonitorId;
        public int OtherChannelId;
        public string OtherUserId = "";
        public string OtherAdminId = "";
        private bool _seeded;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task EnsureSeededAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_seeded) return;
                OtherCustomerId = await this.AddCustomerAsync("Rakip A.Ş.");
                OtherMonitorId = await this.AddMonitorAsync(OtherCustomerId, "SECRET-MONITOR-B", m => { m.Status = MonitorStatus.Down; m.IntervalSeconds = 60; });
                await WithDbAsync(async db =>
                {
                    db.Incidents.Add(new Incident { MonitorId = OtherMonitorId, StartedAtUtc = DateTime.UtcNow.AddMinutes(-3), Cause = "SECRET-CAUSE-B" });
                    var ch = new AlertChannel { CustomerId = OtherCustomerId, Name = "SECRET-CHANNEL-B", Type = AlertChannelType.Email, Target = "ops@rakip.local" };
                    db.AlertChannels.Add(ch);
                    await db.SaveChangesAsync();
                    OtherChannelId = ch.Id;
                });
                OtherUserId = await this.AddUserAsync(OtherCustomerId, "viewer@rakip.local", "Rakip123!", Roles.CustomerViewer);
                OtherAdminId = await this.AddUserAsync(OtherCustomerId, "admin@rakip.local", "Rakip123!", Roles.CustomerAdmin);
                _seeded = true;
            }
            finally { _lock.Release(); }
        }
    }

    private readonly Fixture _f;
    public TenantIsolationTests(Fixture f) => _f = f;

    private async Task<HttpClient> DemoAdminAsync()
    {
        await _f.EnsureSeededAsync();
        return await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
    }

    private async Task AssertMonitorUnchangedAsync()
    {
        var m = (await _f.GetMonitorAsync(_f.OtherMonitorId))!;
        Assert.NotNull(m);
        Assert.Equal("SECRET-MONITOR-B", m.Name);
        Assert.Equal(_f.OtherCustomerId, m.CustomerId);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.True(m.IsEnabled);
        Assert.Equal(60, m.IntervalSeconds);
    }

    [Theory]
    [InlineData("/App/Monitors/Details/{0}")]
    [InlineData("/App/Monitors/Edit/{0}")]
    [InlineData("/App/Monitors/Delete/{0}")]
    public async Task Get_other_customers_monitor_returns_404(string url)
    {
        var c = await DemoAdminAsync();
        var r = await c.GetAsync(string.Format(url, _f.OtherMonitorId));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("/App/Monitors/Edit/{0}")]
    [InlineData("/App/Monitors/Delete/{0}")]
    [InlineData("/App/Monitors/Details/{0}?handler=Pause")]
    [InlineData("/App/Monitors/Details/{0}?handler=Resume")]
    [InlineData("/App/Monitors/Details/{0}?handler=CheckNow")]
    public async Task Post_to_other_customers_monitor_returns_404_and_changes_nothing(string url)
    {
        var c = await DemoAdminAsync();
        var r = await c.PostFormAsync(string.Format(url, _f.OtherMonitorId), MonitorForm("HACKED", ("IntervalSeconds", "300")), "/App/Monitors/Create");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        await AssertMonitorUnchangedAsync();
    }

    [Theory]
    [InlineData("/App/Index")]
    [InlineData("/App/Monitors/Index")]
    [InlineData("/App/Incidents/Index")]
    [InlineData("/App/Alerts/Index")]
    [InlineData("/App/Users/Index")]
    public async Task Lists_do_not_leak_other_customers_data(string url)
    {
        var c = await DemoAdminAsync();
        var html = await c.GetHtmlAsync(url);
        Assert.DoesNotContain("SECRET-", html);
        Assert.DoesNotContain("rakip.local", html);
    }

    [Theory]
    [InlineData("/App/Alerts/Edit/{0}", null)]
    [InlineData("/App/Alerts/Delete/{0}", null)]
    [InlineData("/App/Alerts/Index?handler=Toggle&id={0}", "/App/Alerts/Index")]
    public async Task Other_customers_alert_channel_is_not_accessible(string url, string? formPage)
    {
        var c = await DemoAdminAsync();
        var target = string.Format(url, _f.OtherChannelId);
        if (formPage is null)
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(target)).StatusCode);

        var r = await c.PostFormAsync(target,
            new() { ["Input.Name"] = "HACKED", ["Input.Type"] = "Email", ["Input.Target"] = "x@hacker.local", ["Input.IsEnabled"] = "false" },
            formPage ?? "/App/Alerts/Create");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var ch = await _f.WithDbAsync(db => db.AlertChannels.AsNoTracking().FirstAsync(x => x.Id == _f.OtherChannelId));
        Assert.Equal("SECRET-CHANNEL-B", ch.Name);
        Assert.True(ch.IsEnabled);
        Assert.Equal(_f.OtherCustomerId, ch.CustomerId);
    }

    [Theory]
    [InlineData("ChangeRole")]
    [InlineData("ToggleActive")]
    [InlineData("Delete")]
    public async Task Other_customers_users_cannot_be_managed(string handler)
    {
        var c = await DemoAdminAsync();
        var r = await c.PostFormAsync($"/App/Users/Index?handler={handler}&id={_f.OtherUserId}",
            new() { ["role"] = Roles.CustomerAdmin }, "/App/Users/Index");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var u = await _f.WithDbAsync(db => db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == _f.OtherUserId));
        Assert.NotNull(u);
        Assert.True(u!.IsActive);
        Assert.Equal(_f.OtherCustomerId, u.CustomerId);
        Assert.Equal([Roles.CustomerViewer], await _f.RolesOfAsync(_f.OtherUserId));
    }

    [Fact]
    public async Task Created_monitor_ignores_posted_customer_id()
    {
        var c = await DemoAdminAsync();
        var form = MonitorForm("NotForB");
        form["CustomerId"] = _f.OtherCustomerId.ToString();
        form["Input.CustomerId"] = _f.OtherCustomerId.ToString();
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create", form), "/Details/");
        var owner = await _f.WithDbAsync(db => db.Monitors.Where(m => m.Name == "NotForB").Select(m => m.CustomerId).FirstAsync());
        Assert.Equal(await _f.DemoCustomerIdAsync(), owner);
    }

    [Fact]
    public async Task Other_customer_admin_cannot_see_demo_monitor()
    {
        await _f.EnsureSeededAsync();
        var demoMonitorId = await _f.WithDbAsync(db => db.Monitors.Where(m => m.Name == "Example.com").Select(m => m.Id).FirstAsync());
        var b = await _f.LoginAsync("admin@rakip.local", "Rakip123!");
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/App/Monitors/Details/{demoMonitorId}")).StatusCode);
        var html = await b.GetHtmlAsync("/App/Monitors/Index");
        Assert.Contains("SECRET-MONITOR-B", html);
        Assert.DoesNotContain("Example.com", html);
    }
}
