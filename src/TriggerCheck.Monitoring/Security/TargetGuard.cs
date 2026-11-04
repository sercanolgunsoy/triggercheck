using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TriggerCheck.Monitoring.Security;

/// <summary>Thrown when a probe or webhook would connect to a private/internal address (SSRF protection).</summary>
public sealed class BlockedTargetException() : Exception(TargetGuard.BlockedMessage);

/// <summary>
/// SSRF policy: which destination addresses probes and webhooks may connect to. The real guard runs at connect time
/// (<see cref="ResolveAsync"/> resolves DNS itself and validates every address before a socket is opened), so redirects
/// and DNS rebinding are covered. <see cref="IsObviouslyInternalHost"/> is a cheap input-time check for friendly form errors.
/// Disabled by <c>Monitoring:AllowPrivateTargets=true</c> (development / tests).
/// </summary>
public static class TargetGuard
{
    /// <summary>Error shown to tenants. Deliberately generic: the resolved address is only logged server-side.</summary>
    public const string BlockedMessage = "Blocked target: address is not allowed";

    /// <summary>
    /// True when <paramref name="ip"/> must not be contacted. IPv4: loopback, unspecified, link-local (incl. cloud
    /// metadata), RFC1918, CGNAT, documentation/benchmark ranges, Azure's 168.63.129.16, multicast/broadcast/reserved.
    /// IPv6: allowlist - only global unicast 2000::/3 passes, minus Teredo and documentation prefixes; IPv4 embedded in
    /// IPv6 (mapped, compatible, NAT64 64:ff9b::/96, 6to4 2002::/16) is unwrapped and checked as IPv4.
    /// </summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork) return IsBlockedV4(ip.GetAddressBytes());
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return true;

        var b = ip.GetAddressBytes();
        if (EmbeddedIPv4(ip, b) is { } v4) return IsBlockedV4(v4);
        if ((b[0] & 0xE0) != 0x20) return true;                                   // not 2000::/3 global unicast
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true; // 2001::/32 Teredo
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true; // 2001:db8::/32 documentation
        return false;
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] == 0                                        // 0.0.0.0/8 "this network" (incl. unspecified)
        || b[0] == 10                                    // 10.0.0.0/8
        || b[0] == 127                                   // loopback
        || (b[0] == 169 && b[1] == 254)                  // link-local incl. cloud metadata 169.254.169.254
        || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16.0.0/12
        || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16
        || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10 CGNAT
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 IETF protocol assignments
        || (b[0] == 192 && b[1] == 0 && b[2] == 2)       // 192.0.2.0/24 TEST-NET-1
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)    // 198.51.100.0/24 TEST-NET-2
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)     // 203.0.113.0/24 TEST-NET-3
        || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
        || (b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16) // Azure host/metadata endpoint
        || b[0] >= 224;                                  // multicast, reserved, broadcast

    /// <summary>
    /// IPv4 bytes hidden in an IPv6 address: ::ffff:a.b.c.d (mapped), ::a.b.c.d (compatible, incl. :: and ::1 which
    /// become the blocked 0.0.0.x), 64:ff9b::a.b.c.d (NAT64) and 2002:AABB:CCDD::/48 (6to4).
    /// </summary>
    private static byte[]? EmbeddedIPv4(IPAddress ip, byte[] b)
    {
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4().GetAddressBytes();
        var zeroPrefix = b.Take(12).All(x => x == 0);
        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0);
        if (zeroPrefix || nat64) return b[12..];
        if (b[0] == 0x20 && b[1] == 0x02) return b[2..6];
        return null;
    }

    /// <summary>
    /// Input-time check: a literal blocked IP (any notation <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>
    /// accepts, with or without IPv6 brackets) or an internal-only name such as <c>localhost</c>, <c>*.localhost</c>,
    /// <c>*.local</c> or <c>*.internal</c>. No DNS lookup is done here.
    /// </summary>
    public static bool IsObviouslyInternalHost(string? host)
    {
        var h = host?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(h)) return false;
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        if (IPAddress.TryParse(h, out var ip)) return IsBlocked(ip);
        h = h.ToLowerInvariant();
        return h == "localhost" || h.EndsWith(".localhost") || h.EndsWith(".local") || h.EndsWith(".internal");
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses to connect to. Unless <paramref name="allowPrivate"/>,
    /// throws <see cref="BlockedTargetException"/> when any resolved address is blocked (a mixed answer is treated as hostile);
    /// the host and address are logged, never returned to the caller. DNS failures surface as <see cref="SocketException"/>.
    /// </summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, bool allowPrivate, CancellationToken ct, ILogger? logger = null)
    {
        var h = host.Trim();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        var addresses = IPAddress.TryParse(h, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(h, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        if (allowPrivate) return addresses;

        var blocked = addresses.FirstOrDefault(IsBlocked);
        if (blocked is not null)
        {
            if (logger is not null) LogBlocked(logger, h, blocked);
            throw new BlockedTargetException();
        }
        return addresses;
    }

    /// <summary>Warnings are throttled per host (a blocked monitor retries every few seconds); Debug logs every attempt.</summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);
    private const int MaxThrottledHosts = 10_000;
    private static readonly ConcurrentDictionary<string, long> LastWarningTicks = new(StringComparer.OrdinalIgnoreCase);

    private static void LogBlocked(ILogger logger, string host, IPAddress address)
    {
        var now = Environment.TickCount64;
        var last = LastWarningTicks.GetValueOrDefault(host, long.MinValue);
        if (last == long.MinValue || now - last >= WarningInterval.TotalMilliseconds)
        {
            if (LastWarningTicks.Count >= MaxThrottledHosts) LastWarningTicks.Clear(); // keep memory bounded
            LastWarningTicks[host] = now;
            logger.LogWarning("Blocked outbound connection to {Host} (resolved to {Address}): private or internal address. " +
                              "Further blocks for this host are logged at Debug for {Minutes} minutes.", host, address, WarningInterval.TotalMinutes);
        }
        else
        {
            logger.LogDebug("Blocked outbound connection to {Host} (resolved to {Address})", host, address);
        }
    }

    /// <summary>Opens a TCP connection to a validated address of <paramref name="host"/> (used by probes, webhooks and the TCP checker).</summary>
    public static async Task<Socket> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct, ILogger? logger = null)
    {
        var addresses = await ResolveAsync(host, allowPrivate, ct, logger);
        // Dual-mode socket: works for IPv4 and IPv6 addresses.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary><see cref="SocketsHttpHandler.ConnectCallback"/> that applies the policy to every connection (incl. redirects).</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback(Func<bool> allowPrivate, ILogger? logger = null) =>
        async (context, ct) =>
        {
            var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowPrivate(), ct, logger);
            return new NetworkStream(socket, ownsSocket: true);
        };
}
