using System.Net;
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
