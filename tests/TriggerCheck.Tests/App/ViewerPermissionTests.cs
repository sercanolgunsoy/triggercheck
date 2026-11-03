using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

/// <summary>CustomerViewer is read-only.</summary>
public class ViewerPermissionTests : IClassFixture<ViewerPermissionTests.Fixture>
{
    public const string ViewerEmail = "viewer@demo.local";
    public const string ViewerPassword = "Viewer123!";

    public class Fixture : TestWebAppFactory
    {
        public int MonitorId;
        public int ChannelId;
        private bool _seeded;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task EnsureSeededAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_seeded) return;
                var cid = await this.DemoCustomerIdAsync();
                await this.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
                MonitorId = await this.AddMonitorAsync(cid, "ViewerSees", m => m.Status = MonitorStatus.Up);
                ChannelId = await WithDbAsync(async db =>
                {
                    var ch = new AlertChannel { CustomerId = cid, Name = "ViewerChannel", Type = AlertChannelType.Email, Target = "ops@demo.local" };
                    db.AlertChannels.Add(ch);
                    await db.SaveChangesAsync();
                    return ch.Id;
                });
                _seeded = true;
            }
            finally { _lock.Release(); }
        }
    }

    private readonly Fixture _f;
    public ViewerPermissionTests(Fixture f) => _f = f;

    private async Task<HttpClient> ViewerAsync()
    {
        await _f.EnsureSeededAsync();
        return await _f.LoginAsync(ViewerEmail, ViewerPassword);
    }

    [Theory]
    [InlineData("/App")]
    [InlineData("/App/Index")]
    [InlineData("/App/Monitors/Index")]
    [InlineData("/App/Incidents/Index")]
    [InlineData("/App/Alerts/Index")]
    [InlineData("/App/Account/Index")]
    public async Task Viewer_can_read(string url)
    {
        var c = await ViewerAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Viewer_can_open_monitor_details_without_admin_buttons()
    {
        var c = await ViewerAsync();
        var html = await c.GetHtmlAsync($"/App/Monitors/Details/{_f.MonitorId}");
        Assert.Contains("ViewerSees", html);
        Assert.DoesNotContain("Şimdi kontrol et", html);
        Assert.DoesNotContain("Duraklat", html);
        Assert.DoesNotContain("Düzenle", html);

        var list = await c.GetHtmlAsync("/App/Monitors/Index");
        Assert.DoesNotContain("Yeni monitör", list);
        Assert.DoesNotContain("Düzenle", list);
        Assert.DoesNotContain("Kullanıcılar", list); // nav item is admin-only
    }

    [Theory]
    [InlineData("/App/Monitors/Create")]
    [InlineData("/App/Monitors/Edit/{0}")]
    [InlineData("/App/Monitors/Delete/{0}")]
    [InlineData("/App/Alerts/Create")]
    [InlineData("/App/Users/Index")]
    [InlineData("/App/Users/Create")]
    public async Task Viewer_cannot_open_admin_pages(string url)
    {
        var c = await ViewerAsync();
        AssertAccessDenied(await c.GetAsync(string.Format(url, _f.MonitorId)));
    }

    [Fact]
    public async Task Viewer_cannot_create_monitor()
    {
        var c = await ViewerAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("ViewerCreated"), "/App/Monitors/Index");
        AssertAccessDenied(r);
        Assert.False(await _f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == "ViewerCreated")));
    }

    [Theory]
    [InlineData("/App/Monitors/Delete/{0}")]
    [InlineData("/App/Monitors/Edit/{0}")]
    [InlineData("/App/Monitors/Details/{0}?handler=Pause")]
    [InlineData("/App/Monitors/Details/{0}?handler=CheckNow")]
    public async Task Viewer_cannot_modify_monitor(string url)
    {
        var c = await ViewerAsync();
        var r = await c.PostFormAsync(string.Format(url, _f.MonitorId), MonitorForm("ViewerEdited"), "/App/Monitors/Index");
        AssertAccessDenied(r);
        var m = (await _f.GetMonitorAsync(_f.MonitorId))!;
        Assert.Equal("ViewerSees", m.Name);
        Assert.Equal(MonitorStatus.Up, m.Status);
    }

    [Fact]
    public async Task Viewer_cannot_change_alert_channels()
    {
        var c = await ViewerAsync();
        AssertAccessDenied(await c.PostFormAsync($"/App/Alerts/Index?handler=Toggle&id={_f.ChannelId}", new(), "/App/Alerts/Index"));
        AssertAccessDenied(await c.PostFormAsync($"/App/Alerts/Delete/{_f.ChannelId}", new(), "/App/Alerts/Index"));
        AssertAccessDenied(await c.PostFormAsync("/App/Alerts/Create",
            new() { ["Input.Name"] = "x", ["Input.Type"] = "Email", ["Input.Target"] = "x@demo.local" }, "/App/Alerts/Index"));

        var ch = await _f.WithDbAsync(db => db.AlertChannels.AsNoTracking().FirstAsync(x => x.Id == _f.ChannelId));
        Assert.True(ch.IsEnabled);
        Assert.Equal(1, await _f.WithDbAsync(db => db.AlertChannels.CountAsync()));
    }

    [Fact]
    public async Task Viewer_cannot_manage_users()
    {
        var c = await ViewerAsync();
        var adminId = await _f.UserIdAsync(TestWebAppFactory.DemoAdminEmail);
        AssertAccessDenied(await c.PostFormAsync($"/App/Users/Index?handler=ToggleActive&id={adminId}", new(), "/App/Index"));
        Assert.True(await _f.WithDbAsync(db => db.Users.Where(u => u.Id == adminId).Select(u => u.IsActive).FirstAsync()));
    }

    [Fact]
    public async Task Viewer_can_change_own_password()
    {
        await _f.EnsureSeededAsync();
        var cid = await _f.DemoCustomerIdAsync();
        await _f.AddUserAsync(cid, "pwviewer@demo.local", "Viewer123!", Roles.CustomerViewer);
        var c = await _f.LoginAsync("pwviewer@demo.local", "Viewer123!");

        var bad = await c.PostFormAsync("/App/Account/Index?handler=ChangePassword", new()
        {
            ["Password.CurrentPassword"] = "WrongPass1",
            ["Password.NewPassword"] = "NewViewer123",
            ["Password.ConfirmPassword"] = "NewViewer123"
        }, "/App/Account/Index");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Mevcut şifre hatalı", await bad.ReadHtmlAsync());

        var ok = await c.PostFormAsync("/App/Account/Index?handler=ChangePassword", new()
        {
            ["Password.CurrentPassword"] = "Viewer123!",
            ["Password.NewPassword"] = "NewViewer123",
            ["Password.ConfirmPassword"] = "NewViewer123"
        }, "/App/Account/Index");
        AssertRedirect(ok, "/App/Account");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App/Account/Index")).StatusCode); // still signed in

        await _f.LoginAsync("pwviewer@demo.local", "NewViewer123");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.LoginAsync("pwviewer@demo.local", "Viewer123!"));
    }
}
