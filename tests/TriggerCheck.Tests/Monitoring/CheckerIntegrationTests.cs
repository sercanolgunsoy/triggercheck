using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>Real HTTP/TCP probes against local listeners (no internet). One server shared by the class.</summary>
public class CheckerIntegrationTests : IAsyncLifetime
{
    private LocalHttpServer _server = null!;
    private ServiceProvider _services = null!;
    private readonly List<string> _userAgents = new();

    public async Task InitializeAsync()
    {
        _server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet("/ok", (HttpContext ctx) =>
            {
                lock (_userAgents) _userAgents.Add(ctx.Request.Headers.UserAgent.ToString());
                return Results.Text("<html><body>Welcome to TriggerCheck status</body></html>", "text/html; charset=utf-8");
            });
            app.MapMethods("/head-only", ["HEAD"], () => Results.Ok());
            app.MapGet("/error", () => Results.StatusCode(500));
            app.MapGet("/redirect", () => Results.Redirect("/ok", permanent: true));
            app.MapGet("/slow", async (CancellationToken ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Results.Ok(); });
            app.MapGet("/big", () => Results.Text(new string('a', 2 * 1024 * 1024) + "NEEDLE"));
            app.MapGet("/latin1", () => Results.Bytes(System.Text.Encoding.Latin1.GetBytes("Grüße"), "text/plain; charset=iso-8859-1"));
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMonitoringEngine(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Monitoring:AllowPrivateTargets"] = "true" }).Build());
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _server.DisposeAsync();
    }

    private IMonitorChecker Checker(MonitorType type) => _services.GetServices<IMonitorChecker>().Single(c => c.Type == type);

    private Task<CheckOutcome> HttpAsync(string path, Action<MonitorEntity>? configure = null, MonitorType type = MonitorType.Http)
    {
        var m = new MonitorEntity { Type = type, Target = _server.Url(path), TimeoutSeconds = 5 };
        configure?.Invoke(m);
        return Checker(type).CheckAsync(m, CancellationToken.None);
    }

    [Fact]
    public async Task Http_2xx_is_up_and_sends_user_agent()
    {
        var o = await HttpAsync("/ok");
        Assert.True(o.IsUp, o.Error);
        Assert.Equal(200, o.StatusCode);
        Assert.Null(o.Error);
        Assert.True(o.ResponseTimeMs >= 0);
        lock (_userAgents) Assert.Contains(_userAgents, ua => ua.StartsWith("TriggerCheck/1.0"));
    }

    [Fact]
    public async Task Http_500_is_down_with_status_code()
    {
        var o = await HttpAsync("/error");
        Assert.False(o.IsUp);
        Assert.Equal(500, o.StatusCode);
        Assert.Contains("500", o.Error);
    }

    [Fact]
    public async Task Http_500_is_up_when_expected() =>
        Assert.True((await HttpAsync("/error", m => m.ExpectedStatusCodes = "500")).IsUp);

    [Fact]
    public async Task Http_uses_configured_method()
    {
        Assert.True((await HttpAsync("/head-only", m => m.HttpMethod = "head")).IsUp);
        Assert.Equal(405, (await HttpAsync("/head-only", m => m.HttpMethod = "GET")).StatusCode);
    }

    [Fact]
    public async Task Redirects_are_followed_by_default()
    {
        var o = await HttpAsync("/redirect");
        Assert.True(o.IsUp, o.Error);
        Assert.Equal(200, o.StatusCode);
    }

    [Fact]
    public async Task Redirects_are_not_followed_when_3xx_is_expected()
    {
        var o = await HttpAsync("/redirect", m => m.ExpectedStatusCodes = "301");
        Assert.True(o.IsUp, o.Error);
        Assert.Equal(301, o.StatusCode);
    }

    [Fact]
    public async Task Http_timeout_is_down_with_readable_error()
    {
        var o = await HttpAsync("/slow", m => m.TimeoutSeconds = 1);
        Assert.False(o.IsUp);
        Assert.Null(o.StatusCode);
        Assert.Equal("Timeout after 1s", o.Error);
        // The timeout cannot fire early; "Timeout after 1s" (instead of a 200 after /slow's 10 s) already proves it fired.
        // No wall-clock upper bound: on a loaded machine the cancellation callback itself can be scheduled seconds late.
        Assert.True(o.ResponseTimeMs >= 900, $"{o.ResponseTimeMs} ms");
    }

    [Fact]
    public async Task Http_connection_refused_is_down()
    {
        var port = GetClosedPort();
        var m = new MonitorEntity { Type = MonitorType.Http, Target = $"http://127.0.0.1:{port}/", TimeoutSeconds = 5 };
        var o = await Checker(MonitorType.Http).CheckAsync(m, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.Equal("Connection refused", o.Error);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public async Task Http_invalid_url_is_down_not_exception(string target)
    {
        var o = await Checker(MonitorType.Http).CheckAsync(new MonitorEntity { Type = MonitorType.Http, Target = target }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.StartsWith("Invalid URL", o.Error);
    }

    [Fact]
    public async Task Http_invalid_expected_codes_is_down_not_exception()
    {
        var o = await HttpAsync("/ok", m => m.ExpectedStatusCodes = "abc");
        Assert.False(o.IsUp);
        Assert.StartsWith("Invalid expected status codes", o.Error);
    }

    [Fact]
    public async Task Https_against_plain_http_port_is_down_with_tls_error()
    {
        var url = _server.Url("/ok").Replace("http://", "https://");
        var o = await Checker(MonitorType.Http).CheckAsync(new MonitorEntity { Type = MonitorType.Http, Target = url, TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.False(string.IsNullOrEmpty(o.Error));
    }

    [Theory]
    [InlineData("welcome to triggercheck", true)]   // case-insensitive
    [InlineData("TriggerCheck status", true)]
    [InlineData("Service Unavailable", false)]
    public async Task Keyword_matching(string keyword, bool expectedUp)
    {
        var o = await HttpAsync("/ok", m => m.Keyword = keyword, MonitorType.Keyword);
        Assert.Equal(expectedUp, o.IsUp);
        Assert.Equal(200, o.StatusCode);
        if (!expectedUp) Assert.Contains("not found", o.Error);
    }

    [Fact]
    public async Task Keyword_requires_expected_status_too() =>
        Assert.False((await HttpAsync("/error", m => m.Keyword = "x", MonitorType.Keyword)).IsUp);

    [Fact]
    public async Task Keyword_body_read_is_capped_at_1MB()
    {
        var o = await HttpAsync("/big", m => m.Keyword = "NEEDLE", MonitorType.Keyword);
        Assert.False(o.IsUp);
        Assert.True((await HttpAsync("/big", m => m.Keyword = "aaaa", MonitorType.Keyword)).IsUp);
    }

    [Fact]
    public async Task Keyword_respects_response_charset() =>
        Assert.True((await HttpAsync("/latin1", m => m.Keyword = "grüße", MonitorType.Keyword)).IsUp);

    [Fact]
    public async Task Keyword_without_keyword_configured_is_down() =>
        Assert.False((await HttpAsync("/ok", m => m.Keyword = null, MonitorType.Keyword)).IsUp);

    [Fact]
    public async Task Tcp_open_port_is_up()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        var o = await Checker(MonitorType.Tcp).CheckAsync(new MonitorEntity { Type = MonitorType.Tcp, Target = "127.0.0.1", Port = port, TimeoutSeconds = 5 }, CancellationToken.None);

        Assert.True(o.IsUp, o.Error);
        Assert.Null(o.StatusCode);
        (await accept).Dispose();
    }

    [Fact]
    public async Task Tcp_accepts_pasted_url_as_host()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var o = await Checker(MonitorType.Tcp).CheckAsync(new MonitorEntity { Type = MonitorType.Tcp, Target = "http://127.0.0.1/path", Port = port, TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.True(o.IsUp, o.Error);
    }

    [Fact]
    public async Task Tcp_closed_port_is_down()
    {
        var o = await Checker(MonitorType.Tcp).CheckAsync(new MonitorEntity { Type = MonitorType.Tcp, Target = "127.0.0.1", Port = GetClosedPort(), TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.Equal("Connection refused", o.Error);
    }

    [Theory]
    [InlineData("", 80, "Host is not configured")]
    [InlineData("127.0.0.1", null, "Port is not configured (1-65535)")]
    [InlineData("127.0.0.1", 70000, "Port is not configured (1-65535)")]
    public async Task Tcp_invalid_config_is_down(string host, int? port, string error)
    {
        var o = await Checker(MonitorType.Tcp).CheckAsync(new MonitorEntity { Type = MonitorType.Tcp, Target = host, Port = port }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.Equal(error, o.Error);
    }

    [Fact]
    public async Task Ping_without_host_is_down_not_exception()
    {
        var o = await Checker(MonitorType.Ping).CheckAsync(new MonitorEntity { Type = MonitorType.Ping, Target = " " }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.Equal("Host is not configured", o.Error);
    }

    [Fact]
    public async Task Ping_loopback_never_throws()
    {
        // ICMP may be unavailable (containers / no privileges): only assert that a well-formed outcome comes back.
        var o = await Checker(MonitorType.Ping).CheckAsync(new MonitorEntity { Type = MonitorType.Ping, Target = "127.0.0.1", TimeoutSeconds = 2 }, CancellationToken.None);
        Assert.True(o.IsUp || !string.IsNullOrEmpty(o.Error));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource(200);
        var m = new MonitorEntity { Type = MonitorType.Http, Target = _server.Url("/slow"), TimeoutSeconds = 30 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Checker(MonitorType.Http).CheckAsync(m, cts.Token));
    }

    private static int GetClosedPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
