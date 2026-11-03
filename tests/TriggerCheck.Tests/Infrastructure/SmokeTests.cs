using System.Net;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests;

public class SmokeTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public SmokeTests(TestWebAppFactory f) => _f = f;

    [Fact]
    public async Task Health_returns_ok() =>
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateBrowser().GetAsync("/health")).StatusCode);

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        var r = await _f.CreateBrowser().GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Customer_cannot_open_superadmin_and_vice_versa()
    {
        var customer = await _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/App")).StatusCode);
        Assert.Contains("AccessDenied", (await customer.GetAsync("/SuperAdmin")).Headers.Location!.ToString());

        var sa = await _f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await sa.GetAsync("/SuperAdmin")).StatusCode);
        Assert.Contains("AccessDenied", (await sa.GetAsync("/App")).Headers.Location!.ToString());
    }
}
