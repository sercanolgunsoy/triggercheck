using System.Net;
using Microsoft.Extensions.Logging;
using TriggerCheck.Monitoring.Security;

namespace TriggerCheck.Tests.Security;

public class TargetGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::127.0.0.1")]
    [InlineData("64:ff9b::192.168.0.1")]
    [InlineData("192.0.2.10")]           // TEST-NET-1
    [InlineData("198.51.100.10")]        // TEST-NET-2
    [InlineData("203.0.113.10")]         // TEST-NET-3
    [InlineData("168.63.129.16")]        // Azure host endpoint
    [InlineData("2002:7f00:1::1")]       // 6to4 wrapping 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]    // 6to4 wrapping 169.254.169.254
    [InlineData("2001:0:4136:e378::1")]  // Teredo 2001::/32
    [InlineData("64:ff9b:1::1")]         // local-use NAT64 64:ff9b:1::/48
    [InlineData("100::1")]               // discard-only 100::/64
    [InlineData("2001:db8::1")]          // documentation
    [InlineData("4000::1")]              // outside 2000::/3 (IPv6 allowlist)
    public void Private_and_internal_addresses_are_blocked(string ip) =>
        Assert.True(TargetGuard.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]   // just outside 172.16/12
    [InlineData("100.128.0.1")]  // just outside 100.64/10
    [InlineData("169.255.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::8.8.8.8")]     // NAT64 of a public address
    [InlineData("2002:808:808::1")]      // 6to4 of 8.8.8.8
    [InlineData("2a00:1450:4001::200e")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.False(TargetGuard.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST.", true)]
    [InlineData("api.localhost", true)]
    [InlineData("printer.local", true)]
    [InlineData("metadata.google.internal", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("2130706433", true)]   // decimal notation of 127.0.0.1
    [InlineData("169.254.169.254", true)]
    [InlineData("example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Obviously_internal_hosts_are_detected(string? host, bool expected) =>
        Assert.Equal(expected, TargetGuard.IsObviouslyInternalHost(host));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public async Task Resolve_refuses_private_targets(string host)
    {
        var ex = await Assert.ThrowsAsync<BlockedTargetException>(() => TargetGuard.ResolveAsync(host, allowPrivate: false, CancellationToken.None));
        Assert.StartsWith("Blocked target", ex.Message);
    }

    [Fact]
    public async Task Repeated_blocks_of_a_host_warn_once_then_log_at_debug()
    {
        var logger = new LevelLogger();
        const string host = "10.201.202.203"; // used by no other test (the throttle is process-wide)
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<BlockedTargetException>(() => TargetGuard.ResolveAsync(host, false, CancellationToken.None, logger));

        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug], logger.Levels);
    }

    private sealed class LevelLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    [Fact]
    public async Task Resolve_allows_private_targets_when_flag_is_set()
    {
        var addresses = await TargetGuard.ResolveAsync("127.0.0.1", allowPrivate: true, CancellationToken.None);
        Assert.Equal(IPAddress.Loopback, Assert.Single(addresses));
    }

    [Fact]
    public async Task Resolve_allows_public_literal()
    {
        var addresses = await TargetGuard.ResolveAsync("8.8.8.8", allowPrivate: false, CancellationToken.None);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(addresses));
    }
}
