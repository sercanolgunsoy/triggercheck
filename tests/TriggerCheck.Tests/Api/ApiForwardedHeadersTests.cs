using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Api;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>
/// Host behind a "proxy" at 10.0.0.5 (TestServer has no remote address, so a startup filter sets it) with
/// ForwardedHeaders enabled and a per-IP API limit of 3 per minute.
/// </summary>
public abstract class ProxiedWebAppFactory(string trustedProxy) : TestWebAppFactory
{
    public const string ProxyIp = "10.0.0.5";

    private sealed class FakeRemoteIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = IPAddress.Parse(ProxyIp); return n(ctx); });
            next(app);
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ForwardedHeaders:Enabled", "true");
        builder.UseSetting("ForwardedHeaders:KnownProxies", trustedProxy);
        builder.UseSetting("Api:RequestsPerMinutePerIp", "3");
        builder.ConfigureServices(s => s.AddTransient<IStartupFilter, FakeRemoteIp>());
    }
}

public sealed class TrustedProxyWebAppFactory() : ProxiedWebAppFactory(ProxyIp);
public sealed class UntrustedProxyWebAppFactory() : ProxiedWebAppFactory("10.9.9.9");

public class ApiTrustedProxyTests(TrustedProxyWebAppFactory f) : IClassFixture<TrustedProxyWebAppFactory>
{
    [Fact]
    public async Task Clients_behind_a_trusted_proxy_get_separate_ip_budgets()
    {
        var cid = await f.AddCustomerAsync("Proxied");
        var token = await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly);
        HttpClient Client(string ip) { var c = f.ApiClient(token); c.DefaultRequestHeaders.Add("X-Forwarded-For", ip); return c; }

        var a = Client("203.0.113.1");
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/status")).StatusCode);
        AssertProblem(await a.GetAsync("/api/v1/status"), HttpStatusCode.TooManyRequests);

        Assert.Equal(HttpStatusCode.OK, (await Client("203.0.113.2").GetAsync("/api/v1/status")).StatusCode);
    }
}

public class ApiUntrustedProxyTests(UntrustedProxyWebAppFactory f) : IClassFixture<UntrustedProxyWebAppFactory>
{
    [Fact]
    public async Task Forwarded_header_from_an_untrusted_address_is_ignored()
    {
        var cid = await f.AddCustomerAsync("Spoofer");
        var token = await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly);
        for (var i = 0; i < 3; i++)
        {
            var c = f.ApiClient(token);
            c.DefaultRequestHeaders.Add("X-Forwarded-For", $"198.51.100.{i + 1}"); // a fresh spoofed IP each time
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/status")).StatusCode);
        }
        var spoofed = f.ApiClient(token);
        spoofed.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.99");
        AssertProblem(await spoofed.GetAsync("/api/v1/status"), HttpStatusCode.TooManyRequests);
    }
}

public class ApiClientPartitionTests
{
    [Theory]
    [InlineData("203.0.113.7", "ip:203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "ip:203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "ip6:2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "ip6:2001:db8:1:2::/64")]
    public void Ipv6_is_partitioned_by_slash_64(string ip, string expected) =>
        Assert.Equal(expected, ApiSetup.ClientPartition(IPAddress.Parse(ip)));

    [Fact]
    public void Missing_address_has_its_own_partition() => Assert.Equal("ip:unknown", ApiSetup.ClientPartition(null));
}
