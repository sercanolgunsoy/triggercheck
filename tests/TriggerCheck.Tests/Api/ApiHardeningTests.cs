using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.SuperAdmin;
using TriggerCheck.Web.Infrastructure;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>Body / path edge cases and the SuperAdmin key list.</summary>
public class ApiEdgeCaseTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Theory]
    [InlineData("application/json; charset=bogus")]
    [InlineData("application/json; charset=utf-7000")]
    public async Task Unknown_charset_is_415_problem(string contentType)
    {
        var cid = await f.AddCustomerAsync("Charset-" + Guid.NewGuid().ToString("N")[..6]);
        var c = f.ApiClient(await f.AddApiKeyAsync(cid));
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"x\"}"));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        AssertProblem(await c.PostAsync("/api/v1/monitors", content), HttpStatusCode.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("/API/v1/monitors")]
    [InlineData("/Api/V1/Monitors")]
    public async Task Mixed_case_api_paths_are_still_api_requests(string path)
    {
        var cid = await f.AddCustomerAsync("Case-" + Guid.NewGuid().ToString("N")[..6]);
        await f.AddMonitorAsync(cid, "CaseMonitor");
        Assert.Equal(HttpStatusCode.OK, (await f.ApiClient(await f.AddApiKeyAsync(cid)).GetAsync(path)).StatusCode);

        // A browser session is not forwarded to the cookie on any spelling of the path.
        var browser = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        AssertProblem(await browser.GetAsync(path), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SuperAdmin_lists_and_revokes_all_keys_of_a_customer()
    {
        var cid = await f.AddCustomerAsync("KeysAdmin-" + Guid.NewGuid().ToString("N")[..6]);
        var a = await f.AddApiKeyAsync(cid);
        var b = await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly);
        var other = await f.AddApiKeyAsync(await f.DemoCustomerIdAsync());

        var sa = await f.LoginSuperAdminAsync();
        var page = await sa.GetHtmlAsync($"/SuperAdmin/Customers/ApiKeys/{cid}");
        Assert.Contains("tc_" + a.Split('_', 3)[1], page);
        Assert.DoesNotContain(a, page); // never the secret
        Assert.Contains($"/SuperAdmin/Customers/ApiKeys/{cid}", await sa.GetHtmlAsync($"/SuperAdmin/Customers/Details/{cid}"));

        var r = await sa.PostFormAsync($"/SuperAdmin/Customers/ApiKeys/{cid}?handler=RevokeAll", new(), $"/SuperAdmin/Customers/ApiKeys/{cid}");
        AppTestData.AssertRedirect(r, $"/SuperAdmin/Customers/ApiKeys/{cid}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.ApiClient(a).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.ApiClient(b).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.ApiClient(other).GetAsync("/api/v1/status")).StatusCode);

        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        AppTestData.AssertAccessDenied(await admin.GetAsync($"/SuperAdmin/Customers/ApiKeys/{cid}"));
    }
}

/// <summary>Per-IP limit (before authentication): floods of rejected requests are throttled too.</summary>
public sealed class IpLimitedWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Api:RequestsPerMinutePerIp", "10");
    }
}

public class ApiIpRateLimitTests(IpLimitedWebAppFactory f) : IClassFixture<IpLimitedWebAppFactory>
{
    [Fact]
    public async Task Unauthenticated_flood_gets_429()
    {
        var c = f.ApiClient("tc_abcdefghij_invalid");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 14; i++) statuses.Add((await c.GetAsync("/api/v1/monitors")).StatusCode);

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        var limited = await c.GetAsync("/api/v1/status");
        AssertProblem(limited, HttpStatusCode.TooManyRequests);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Pages are not subject to the API limit.
        Assert.Equal(HttpStatusCode.OK, (await f.CreateBrowser().GetAsync("/Account/Login")).StatusCode);
    }
}

/// <summary>Per-customer limit across all keys of a customer.</summary>
public sealed class CustomerLimitedWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Api:RequestsPerMinutePerCustomer", "4");
    }
}

public class ApiCustomerRateLimitTests(CustomerLimitedWebAppFactory f) : IClassFixture<CustomerLimitedWebAppFactory>
{
    [Fact]
    public async Task Keys_of_one_customer_share_the_customer_budget()
    {
        var cid = await f.AddCustomerAsync("Shared");
        var k1 = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));
        var k2 = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await k1.GetAsync("/api/v1/monitors")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await k2.GetAsync("/api/v1/monitors")).StatusCode);
        }
        AssertProblem(await k2.GetAsync("/api/v1/monitors"), HttpStatusCode.TooManyRequests);

        // Another customer is unaffected.
        var other = f.ApiClient(await f.AddApiKeyAsync(await f.AddCustomerAsync("Other"), ApiKeyScope.ReadOnly));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/monitors")).StatusCode);
    }
}

/// <summary>An unhandled exception in an API handler becomes a generic 500 problem without details.</summary>
public sealed class ThrowingApiWebAppFactory : TestWebAppFactory
{
    public const string Secret = "internal-detail-must-not-leak";

    private sealed class ThrowingCurrentCustomer : ICurrentCustomer
    {
        public int? CustomerId => throw new InvalidOperationException(Secret);
        public int RequiredCustomerId => throw new InvalidOperationException(Secret);
        public Task<Customer> GetCustomerAsync(CancellationToken ct = default) => throw new InvalidOperationException(Secret);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddScoped<ICurrentCustomer, ThrowingCurrentCustomer>());
    }
}

public class ApiExceptionTests(ThrowingApiWebAppFactory f) : IClassFixture<ThrowingApiWebAppFactory>
{
    [Fact]
    public async Task Unhandled_exception_is_a_generic_500_problem()
    {
        var cid = await f.WithDbAsync(db => db.Customers.Select(c => c.Id).FirstAsync());
        var c = f.ApiClient(await f.AddApiKeyAsync(cid));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var r = await c.GetAsync("/api/v1/monitors");
        AssertProblem(r, HttpStatusCode.InternalServerError);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ThrowingApiWebAppFactory.Secret, body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }
}
