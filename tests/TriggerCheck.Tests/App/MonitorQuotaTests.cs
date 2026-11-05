using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

/// <summary>Own factory: fills the demo customer up to its plan quota.</summary>
public class MonitorQuotaTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public MonitorQuotaTests(TestWebAppFactory f) => _f = f;

    [Fact]
    public async Task Cannot_create_more_monitors_than_plan_allows()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var max = await _f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.Plan.MaxMonitors).FirstAsync());
        var count = await _f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid));
        var c = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        // Fill up to max - 1 directly, the last slot through the UI.
        for (var i = count; i < max - 1; i++) await _f.AddMonitorAsync(cid, $"Fill-{i}");
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create", MonitorForm("LastSlot")), "/Details/");
        Assert.Equal(max, await _f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));

        var page = await c.GetHtmlAsync("/App/Monitors/Create");
        Assert.Contains("You have reached the monitor limit of your plan", page);

        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("OverQuota"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("You have reached the monitor limit of your plan", await r.ReadHtmlAsync());
        Assert.Equal(max, await _f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));
    }

    [Fact]
    public async Task Parallel_creates_never_exceed_the_plan_quota()
    {
        using var f = new TestWebAppFactory(); // own database: fills the quota
        var cid = await f.DemoCustomerIdAsync();
        var max = await f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).Select(x => x.Plan.MaxMonitors).FirstAsync());
        var count = await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid));
        for (var i = count; i < max - 3; i++) await f.AddMonitorAsync(cid, $"Fill-{i}");
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var page = await c.GetStringAsync("/App/Monitors/Create");
        var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
        {
            var form = new Dictionary<string, string>(MonitorForm($"Parallel-{i}")) { ["__RequestVerificationToken"] = token };
            return c.PostAsync("/App/Monitors/Create", new FormUrlEncodedContent(form));
        }));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"Unexpected {(int)r.StatusCode}"));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Redirect));
        Assert.Equal(max, await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));
    }
}
