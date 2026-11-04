using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Security;

/// <summary>Production wiring (Monitoring:AllowPrivateTargets unset = false) refuses private destinations at connect time.</summary>
public class SsrfProbeTests : IAsyncLifetime
{
    private LocalHttpServer _target = null!;
    private LocalHttpServer _redirector = null!;
    private int _targetHits;

    public async Task InitializeAsync()
    {
        _target = await LocalHttpServer.StartAsync(app => app.MapGet("/secret", () =>
        {
            Interlocked.Increment(ref _targetHits);
            return Results.Text("internal secret");
        }));
        _redirector = await LocalHttpServer.StartAsync(app => app.MapGet("/go", () => Results.Redirect(_target.Url("/secret"))));
    }

    public async Task DisposeAsync()
    {
        await _redirector.DisposeAsync();
        await _target.DisposeAsync();
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMonitoringEngine(new ConfigurationBuilder().Build());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static IMonitorChecker Checker(ServiceProvider sp, MonitorType type) => sp.GetServices<IMonitorChecker>().Single(c => c.Type == type);

    [Theory]
    [InlineData(MonitorType.Http)]
    [InlineData(MonitorType.Keyword)]
    public async Task Http_probe_to_loopback_is_blocked(MonitorType type)
    {
        await using var sp = Build();
        var o = await Checker(sp, type).CheckAsync(new MonitorEntity { Type = type, Target = _target.Url("/secret"), Keyword = "secret", TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.StartsWith("Blocked target", o.Error);
        Assert.Equal(0, _targetHits);
    }

    [Theory]
    [InlineData("http://localhost:1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:127.0.0.1]:1/")]
    [InlineData("http://10.0.0.1:1/")]
    public async Task Http_probe_to_internal_addresses_is_blocked_without_connecting(string url)
    {
        await using var sp = Build();
        var o = await Checker(sp, MonitorType.Http).CheckAsync(new MonitorEntity { Type = MonitorType.Http, Target = url, TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.StartsWith("Blocked target", o.Error);
    }

    [Fact]
    public async Task Redirect_to_loopback_is_blocked()
    {
        // The first connection (to the redirector) is allowed, every later one is not: proves each redirect hop
        // goes through the connect-time guard of the production probe client.
        var options = new FirstConnectionAllowed();
        await using var sp = Build(s => s.AddSingleton<IOptionsMonitor<MonitoringOptions>>(options));

        var o = await Checker(sp, MonitorType.Keyword).CheckAsync(
            new MonitorEntity { Type = MonitorType.Keyword, Target = _redirector.Url("/go"), Keyword = "secret", TimeoutSeconds = 5 }, CancellationToken.None);

        Assert.False(o.IsUp);
        Assert.StartsWith("Blocked target: 127.0.0.1", o.Error);
        Assert.Equal(0, _targetHits);
        Assert.True(options.Reads >= 2);
    }

    [Fact]
    public async Task Tcp_probe_to_loopback_is_blocked()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var sp = Build();

        var o = await Checker(sp, MonitorType.Tcp).CheckAsync(new MonitorEntity { Type = MonitorType.Tcp, Target = "localhost", Port = port, TimeoutSeconds = 5 }, CancellationToken.None);

        Assert.False(o.IsUp);
        Assert.StartsWith("Blocked target", o.Error);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.0.1")]
    public async Task Ping_probe_to_private_address_is_blocked(string host)
    {
        await using var sp = Build();
        var o = await Checker(sp, MonitorType.Ping).CheckAsync(new MonitorEntity { Type = MonitorType.Ping, Target = host, TimeoutSeconds = 2 }, CancellationToken.None);
        Assert.False(o.IsUp);
        Assert.StartsWith("Blocked target", o.Error);
    }

    [Fact]
    public async Task Webhook_to_loopback_is_blocked()
    {
        var hits = 0;
        await using var hook = await LocalHttpServer.StartAsync(app => app.MapPost("/hook", () => { Interlocked.Increment(ref hits); return Results.Ok(); }));
        await using var sp = Build();
        var webhook = sp.GetServices<INotifier>().Single(n => n.Type == AlertChannelType.Webhook);

        var r = await webhook.SendAsync(hook.Url("/hook"),
            new AlertMessage(1, 1, "M", "https://example.org", AlertKind.Down, "x", DateTime.UtcNow), CancellationToken.None);

        Assert.False(r.Success);
        Assert.StartsWith("Blocked target", r.Error);
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task Flag_allows_private_targets()
    {
        await using var sp = Build(s => s.Configure<MonitoringOptions>(o => o.AllowPrivateTargets = true));
        var o = await Checker(sp, MonitorType.Http).CheckAsync(new MonitorEntity { Type = MonitorType.Http, Target = _target.Url("/secret"), TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.True(o.IsUp, o.Error);
    }

    /// <summary>AllowPrivateTargets is true only for the first read (= the first connection).</summary>
    private sealed class FirstConnectionAllowed : IOptionsMonitor<MonitoringOptions>
    {
        public int Reads;
        public MonitoringOptions CurrentValue => new() { AllowPrivateTargets = Interlocked.Increment(ref Reads) == 1 };
        public MonitoringOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MonitoringOptions, string?> listener) => null;
    }
}
