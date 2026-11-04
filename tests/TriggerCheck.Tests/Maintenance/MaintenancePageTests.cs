using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Maintenance;

/// <summary>FEAT-3: maintenance window pages (admin CRUD, viewer read-only, tenant isolation, badges).</summary>
public class MaintenancePageTests(MaintenancePageTests.Fixture f) : IClassFixture<MaintenancePageTests.Fixture>
{
    public const string ViewerEmail = "mw-viewer@demo.local";
    public const string ViewerPassword = "Viewer123!";

    public class Fixture : TestWebAppFactory
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _seeded;

        public async Task EnsureSeededAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_seeded) return;
                await this.AddUserAsync(await this.DemoCustomerIdAsync(), ViewerEmail, ViewerPassword, Roles.CustomerViewer);
                _seeded = true;
            }
            finally { _lock.Release(); }
        }
    }

    private static string Local(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm");

    private Task<MaintenanceWindow?> FindAsync(string name) =>
        f.WithDbAsync(db => db.MaintenanceWindows.AsNoTracking().Include(w => w.Monitors).FirstOrDefaultAsync(w => w.Name == name));

    private Task<int> AddWindowAsync(int customerId, string name, DateTime start, DateTime end, int? monitorId = null) =>
        f.WithDbAsync(async db =>
        {
            var w = new MaintenanceWindow { CustomerId = customerId, Name = name, StartsAtUtc = start, EndsAtUtc = end, AppliesToAllMonitors = monitorId is null, TimeZoneId = "UTC" };
            if (monitorId is int id) w.Monitors.Add(await db.Monitors.SingleAsync(m => m.Id == id));
            db.MaintenanceWindows.Add(w);
            await db.SaveChangesAsync();
            return w.Id;
        });

    [Fact]
    public async Task Admin_creates_a_one_off_window_for_selected_monitors_in_a_time_zone()
    {
        var cid = await f.DemoCustomerIdAsync();
        var monitorId = await f.AddMonitorAsync(cid, "MW-Target");
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(10);

        var r = await admin.PostFormAsync("/App/Maintenance/Create", new()
        {
            ["Input.Name"] = "DB upgrade",
            ["Input.TimeZoneId"] = "Europe/Istanbul",
            ["Input.IsRecurring"] = "false",
            ["Input.StartsLocal"] = Local(start),
            ["Input.EndsLocal"] = Local(start.AddHours(2)),
            ["Input.AllMonitors"] = "false",
            ["Input.MonitorIds"] = monitorId.ToString()
        });
        AssertRedirect(r, "/App/Maintenance");

        var w = await FindAsync("DB upgrade");
        Assert.NotNull(w);
        Assert.Equal(cid, w.CustomerId);
        Assert.False(w.IsRecurring);
        Assert.Equal(start.AddHours(-3), w.StartsAtUtc); // Istanbul is UTC+3
        Assert.Equal(start.AddHours(-1), w.EndsAtUtc);
        Assert.Equal([monitorId], w.Monitors.Select(m => m.Id));

        var list = await admin.GetHtmlAsync("/App/Maintenance");
        Assert.Contains("DB upgrade", list);
        Assert.Contains("Upcoming", list);
    }

    [Fact]
    public async Task Admin_creates_and_edits_a_weekly_window()
    {
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var r = await admin.PostFormAsync("/App/Maintenance/Create", new()
        {
            ["Input.Name"] = "Weekly backup",
            ["Input.TimeZoneId"] = "Europe/Berlin",
            ["Input.IsRecurring"] = "true",
            ["Input.Days"] = "Sunday",
            ["Input.StartTime"] = "02:30",
            ["Input.DurationMinutes"] = "90",
            ["Input.AllMonitors"] = "true"
        });
        AssertRedirect(r, "/App/Maintenance");
        var w = (await FindAsync("Weekly backup"))!;
        Assert.True(w.IsRecurring);
        Assert.Equal(WeekDays.Sunday, w.RecurrenceDays);
        Assert.Equal(150, w.RecurrenceStartMinutes);
        Assert.Equal(90, w.DurationMinutes);
        Assert.True(w.AppliesToAllMonitors);
        Assert.Null(w.StartsAtUtc);

        r = await admin.PostFormAsync($"/App/Maintenance/Edit/{w.Id}", new()
        {
            ["Input.Name"] = "Weekly backup",
            ["Input.TimeZoneId"] = "Europe/Berlin",
            ["Input.IsRecurring"] = "true",
            ["Input.Days"] = "Saturday",
            ["Input.StartTime"] = "23:00",
            ["Input.DurationMinutes"] = "60",
            ["Input.AllMonitors"] = "true"
        });
        AssertRedirect(r, "/App/Maintenance");
        w = (await FindAsync("Weekly backup"))!;
        Assert.Equal(WeekDays.Saturday, w.RecurrenceDays);
        Assert.Equal(23 * 60, w.RecurrenceStartMinutes);
    }

    [Theory]
    [InlineData("true", "", "02:00", "60", "Choose at least one day.")]
    [InlineData("true", "Monday", "25:00", "60", "Enter the start time as HH:mm.")]
    [InlineData("true", "Monday", "02:00", "0", "The duration must be between 1 and 10080 minutes.")]
    [InlineData("false", "", "", "", "The end must be after the start.")]
    public async Task Invalid_input_is_rejected(string recurring, string day, string time, string duration, string error)
    {
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var start = DateTime.UtcNow.AddDays(1);
        var form = new Dictionary<string, string>
        {
            ["Input.Name"] = "Invalid " + error,
            ["Input.TimeZoneId"] = "UTC",
            ["Input.IsRecurring"] = recurring,
            ["Input.StartTime"] = time,
            ["Input.DurationMinutes"] = duration,
            ["Input.StartsLocal"] = Local(start),
            ["Input.EndsLocal"] = Local(start.AddHours(-1)),
            ["Input.AllMonitors"] = "true"
        };
        if (day != "") form["Input.Days"] = day;
        var r = await admin.PostFormAsync("/App/Maintenance/Create", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(error, await r.ReadHtmlAsync());
        Assert.Null(await FindAsync("Invalid " + error));
    }

    [Fact]
    public async Task Viewer_sees_windows_but_cannot_change_them()
    {
        await f.EnsureSeededAsync();
        var id = await AddWindowAsync(await f.DemoCustomerIdAsync(), "Viewer visible", DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(1));
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);

        var list = await viewer.GetHtmlAsync("/App/Maintenance");
        Assert.Contains("Viewer visible", list);
        Assert.DoesNotContain("/App/Maintenance/Create", list);
        AssertAccessDenied(await viewer.GetAsync("/App/Maintenance/Create"));
        AssertAccessDenied(await viewer.GetAsync($"/App/Maintenance/Edit/{id}"));
        AssertAccessDenied(await viewer.GetAsync($"/App/Maintenance/Delete/{id}"));
        Assert.NotNull(await FindAsync("Viewer visible"));
    }

    [Fact]
    public async Task Other_tenants_windows_and_monitors_are_out_of_reach()
    {
        var other = await f.AddCustomerAsync("MW Other " + Guid.NewGuid().ToString("N")[..6]);
        var foreignMonitor = await f.AddMonitorAsync(other, "Foreign");
        var foreignWindow = await AddWindowAsync(other, "Foreign window", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        Assert.DoesNotContain("Foreign window", await admin.GetHtmlAsync("/App/Maintenance"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/App/Maintenance/Edit/{foreignWindow}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/App/Maintenance/Delete/{foreignWindow}")).StatusCode);
        var del = await admin.PostFormAsync($"/App/Maintenance/Delete/{foreignWindow}", new(), "/App/Maintenance/Create");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
        Assert.NotNull(await FindAsync("Foreign window"));

        // Selecting another tenant's monitor is ignored (nothing of ours selected -> validation error).
        var start = DateTime.UtcNow.AddDays(2);
        var r = await admin.PostFormAsync("/App/Maintenance/Create", new()
        {
            ["Input.Name"] = "Steal",
            ["Input.TimeZoneId"] = "UTC",
            ["Input.IsRecurring"] = "false",
            ["Input.StartsLocal"] = Local(start),
            ["Input.EndsLocal"] = Local(start.AddHours(1)),
            ["Input.AllMonitors"] = "false",
            ["Input.MonitorIds"] = foreignMonitor.ToString()
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(await FindAsync("Steal"));
    }

    [Fact]
    public async Task Admin_deletes_a_window()
    {
        var id = await AddWindowAsync(await f.DemoCustomerIdAsync(), "To delete", DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(1).AddHours(1));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        AssertRedirect(await admin.PostFormAsync($"/App/Maintenance/Delete/{id}", new()), "/App/Maintenance");
        Assert.Null(await FindAsync("To delete"));
    }

    [Fact]
    public async Task Monitor_in_an_active_window_shows_a_badge_on_the_list_and_details()
    {
        var cid = await f.DemoCustomerIdAsync();
        var inside = await f.AddMonitorAsync(cid, "MW-Badge-In", m => m.Status = MonitorStatus.Up);
        await AddWindowAsync(cid, "Now", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddHours(1), inside);
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var list = await admin.GetHtmlAsync("/App/Monitors");
        var name = list.IndexOf("MW-Badge-In", StringComparison.Ordinal);
        var rowStart = list.LastIndexOf("<tr>", name, StringComparison.Ordinal);
        Assert.Contains("maintenance-badge", list[rowStart..name]);

        Assert.Contains("id=\"maintenance-badge\"", await admin.GetHtmlAsync($"/App/Monitors/Details/{inside}"));
        var outside = await f.AddMonitorAsync(cid, "MW-Badge-Out", m => m.Status = MonitorStatus.Up);
        Assert.DoesNotContain("id=\"maintenance-badge\"", await admin.GetHtmlAsync($"/App/Monitors/Details/{outside}"));
    }

    [Theory]
    [InlineData("America")]
    [InlineData("Europe/../../etc")]
    [InlineData("Not/AZone")]
    public async Task Unknown_or_partial_time_zones_are_rejected(string zone)
    {
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var start = DateTime.UtcNow.AddDays(1);
        var name = "TZ " + Guid.NewGuid().ToString("N")[..6];
        var r = await admin.PostFormAsync("/App/Maintenance/Create", new()
        {
            ["Input.Name"] = name, ["Input.TimeZoneId"] = zone, ["Input.IsRecurring"] = "false",
            ["Input.StartsLocal"] = Local(start), ["Input.EndsLocal"] = Local(start.AddHours(1)), ["Input.AllMonitors"] = "true"
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Choose a valid time zone.", await r.ReadHtmlAsync());
        Assert.Null(await FindAsync(name));
    }

    [Fact]
    public async Task An_ended_window_can_be_renamed_but_not_moved_into_the_past()
    {
        var start = DateTime.UtcNow.Date.AddDays(-3).AddHours(10);
        var id = await AddWindowAsync(await f.DemoCustomerIdAsync(), "Ended " + Guid.NewGuid().ToString("N")[..6], start, start.AddHours(1));
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var form = new Dictionary<string, string>
        {
            ["Input.Name"] = "Renamed ended window", ["Input.TimeZoneId"] = "UTC", ["Input.IsRecurring"] = "false",
            ["Input.StartsLocal"] = Local(start), ["Input.EndsLocal"] = Local(start.AddHours(1)), ["Input.AllMonitors"] = "true"
        };
        AssertRedirect(await admin.PostFormAsync($"/App/Maintenance/Edit/{id}", form), "/App/Maintenance");
        Assert.NotNull(await FindAsync("Renamed ended window"));

        form["Input.EndsLocal"] = Local(start.AddHours(2)); // changed, still in the past
        var r = await admin.PostFormAsync($"/App/Maintenance/Edit/{id}", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("The end must be in the future.", await r.ReadHtmlAsync());
    }
}
