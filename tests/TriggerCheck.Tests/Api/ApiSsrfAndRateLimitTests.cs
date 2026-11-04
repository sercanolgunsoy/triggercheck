using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Security;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>The SSRF pre-check of the monitor form also applies to the API (guard on, production default).</summary>
public class ApiSsrfTests(GuardedWebAppFactory f) : IClassFixture<GuardedWebAppFactory>
{
    [Theory]
    [InlineData("Http", "http://127.0.0.1:8765/")]
    [InlineData("Http", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("Keyword", "https://192.168.1.10/status")]
    [InlineData("Tcp", "10.0.0.1")]
    [InlineData("Ping", "localhost")]
    public async Task Private_target_is_rejected_on_create_and_update(string type, string target)
    {
        var cid = await f.AddCustomerAsync("Ssrf-" + Guid.NewGuid().ToString("N")[..6]);
        var c = f.ApiClient(await f.AddApiKeyAsync(cid));
        var body = MonitorBody("Ssrf", ("type", type), ("target", target), ("port", 80), ("keyword", "ok"));

        var r = await c.PostJsonAsync("/api/v1/monitors", body);
        AssertProblem(r, HttpStatusCode.BadRequest);
        Assert.Equal("This address points to a private or internal network and cannot be monitored.",
            (await r.JsonAsync()).GetProperty("errors").GetProperty("target")[0].GetString());
        Assert.False(await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.CustomerId == cid)));

        var id = await f.AddMonitorAsync(cid, "Public");
        AssertProblem(await c.PutJsonAsync($"/api/v1/monitors/{id}", body), HttpStatusCode.BadRequest);
        Assert.StartsWith("https://example.org/", (await f.GetMonitorAsync(id))!.Target);
    }
}

public sealed class RateLimitedWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Api:RequestsPerMinute", "3");
    }
}

public class ApiRateLimitTests(RateLimitedWebAppFactory f) : IClassFixture<RateLimitedWebAppFactory>
{
    [Fact]
    public async Task Limit_is_per_key_with_429_and_retry_after()
    {
        var cid = await f.AddCustomerAsync("Rate");
        var c = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/monitors")).StatusCode);

        var limited = await c.GetAsync("/api/v1/monitors");
        AssertProblem(limited, HttpStatusCode.TooManyRequests);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);

        // Another key (even of the same customer) has its own budget.
        var other = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/status")).StatusCode);
    }
}
