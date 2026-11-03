using System.Net;
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
        Assert.Contains("monitör limitine ulaştınız", page);

        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("OverQuota"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("monitör limitine ulaştınız", await r.ReadHtmlAsync());
        Assert.Equal(max, await _f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));
    }
}
