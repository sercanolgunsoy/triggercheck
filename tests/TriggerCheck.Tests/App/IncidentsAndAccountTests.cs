using System.Net;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Localization;

using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

public class IncidentsAndAccountTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public IncidentsAndAccountTests(TestWebAppFactory f) => _f = f;

    private Task<HttpClient> AdminAsync() => _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    [Fact]
    public async Task Incidents_list_filters_and_pages()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "IncidentMonitor");
        await _f.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = now.AddMinutes(-7), Cause = "OPEN-ONE" });
            for (var i = 0; i < 60; i++)
                db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = now.AddDays(-1).AddHours(-i), ResolvedAtUtc = now.AddDays(-1).AddHours(-i).AddMinutes(65), Cause = $"RESOLVED-{i:00}" });
            await db.SaveChangesAsync();
        });
        var c = await AdminAsync();

        var page1 = await c.GetHtmlAsync("/App/Incidents/Index");
        Assert.Contains("OPEN-ONE", page1);
        Assert.True(page1.IndexOf("OPEN-ONE", StringComparison.Ordinal) < page1.IndexOf("RESOLVED-00", StringComparison.Ordinal), "open incidents first");
        Assert.Contains("1 h 5 min", page1);
        Assert.Contains("Page 1 / 2", page1);
        Assert.DoesNotContain("RESOLVED-59", page1);

        var page2 = await c.GetHtmlAsync("/App/Incidents/Index?p=2");
        Assert.Contains("RESOLVED-59", page2);

        var open = await c.GetHtmlAsync("/App/Incidents/Index?filter=open");
        Assert.Contains("OPEN-ONE", open);
        Assert.DoesNotContain("RESOLVED-", open);

        var resolved = await c.GetHtmlAsync("/App/Incidents/Index?filter=resolved");
        Assert.DoesNotContain("OPEN-ONE", resolved);
        Assert.Contains("RESOLVED-00", resolved);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App/Incidents/Index?p=999")).StatusCode);
    }

    [Fact]
    public async Task Account_page_shows_plan()
    {
        var html = await (await AdminAsync()).GetHtmlAsync("/App/Account/Index");
        Assert.Contains("Standard", html);
        Assert.Contains("Demo Company", html);
        Assert.Contains("Contact support to change your plan", html);
        Assert.Contains("1 min", html);
        Assert.Contains("5 min", html);
        Assert.Contains("90 days", html);
    }

    [Theory]
    [InlineData(30, "30 sec", "30 sn")]
    [InlineData(59, "59 sec", "59 sn")]
    [InlineData(60, "1 min", "1 dk")]
    [InlineData(65 * 60, "1 h 5 min", "1 sa 5 dk")]
    [InlineData(2 * 3600, "2 h", "2 sa")]
    [InlineData(26 * 3600, "1 d 2 h", "1 gün 2 sa")]
    [InlineData(48 * 3600, "2 d", "2 gün")]
    public void Duration_is_formatted_in_the_ui_language(int seconds, string english, string turkish)
    {
        _ = _f.Services; // starts the host, which configures Fmt's localizer
        using (new UiCulture("en")) Assert.Equal(english, Fmt.Duration(TimeSpan.FromSeconds(seconds)));
        using (new UiCulture("tr")) Assert.Equal(turkish, Fmt.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Times_are_shown_in_istanbul_time()
    {
        _ = _f.Services;
        var utc = new DateTime(2026, 11, 3, 12, 4, 5, DateTimeKind.Utc);
        using (new UiCulture("en")) Assert.Equal("2026-11-03 15:04:05", Fmt.Time(utc));
        using (new UiCulture("tr")) Assert.Equal("03.11.2026 15:04:05", Fmt.Time(utc));
    }
}
