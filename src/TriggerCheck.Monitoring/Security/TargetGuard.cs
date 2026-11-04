using System.Net;
using System.Net.Sockets;

namespace TriggerCheck.Monitoring.Security;

/// <summary>Thrown when a probe or webhook would connect to a private/internal address (SSRF protection).</summary>
public sealed class BlockedTargetException(string message) : Exception(message);

/// <summary>
/// SSRF policy: which destination addresses probes and webhooks may connect to. The real guard runs at connect time
/// (<see cref="ResolveAsync"/> resolves DNS itself and validates every address before a socket is opened), so redirects
/// and DNS rebinding are covered. <see cref="IsObviouslyInternalHost"/> is a cheap input-time check for friendly form errors.
/// Disabled by <c>Monitoring:AllowPrivateTargets=true</c> (development / tests).
/// </summary>
public static class TargetGuard
{
    public const string BlockedPrefix = "Blocked target";

    /// <summary>True when <paramref name="ip"/> is loopback, unspecified, link-local, private (RFC1918, CGNAT, ULA),
    /// multicast/broadcast or otherwise reserved. IPv4-mapped / -compatible / NAT64 IPv6 forms are unwrapped first.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var embedded = EmbeddedIPv4(ip);
            if (embedded is not null) return IsBlockedV4(embedded.GetAddressBytes());
            return IsBlockedV6(ip);
        }
        return ip.AddressFamily != AddressFamily.InterNetwork || IsBlockedV4(ip.GetAddressBytes());
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
        || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
        || b[0] >= 224;                                  // multicast, reserved, broadcast

    private static bool IsBlockedV6(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
        var b = ip.GetAddressBytes();
        return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    /// <summary>IPv4 address hidden in an IPv6 one: ::ffff:a.b.c.d (mapped), ::a.b.c.d (compatible, incl. :: and ::1), 64:ff9b::a.b.c.d (NAT64).</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        var zeroPrefix = b.Take(12).All(x => x == 0);
        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0);
        // :: and ::1 become 0.0.0.0 / 0.0.0.1, which are blocked as "this network".
        return zeroPrefix || nat64 ? new IPAddress(b[12..]) : null;
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
    /// throws <see cref="BlockedTargetException"/> when any resolved address is blocked (a mixed answer is treated as hostile).
    /// DNS failures surface as <see cref="SocketException"/> like a normal connect would.
    /// </summary>
    public static async Task<IPAddress[]> ResolveAsync(string host, bool allowPrivate, CancellationToken ct)
    {
        var h = host.Trim();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        var addresses = IPAddress.TryParse(h, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(h, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        if (allowPrivate) return addresses;

        var blocked = addresses.FirstOrDefault(IsBlocked);
        if (blocked is not null)
            throw new BlockedTargetException(literal is not null
                ? $"{BlockedPrefix}: {h} is a private or internal address"
                : $"{BlockedPrefix}: {h} resolves to a private or internal address ({blocked})");
        return addresses;
    }

    /// <summary>Opens a TCP connection to a validated address of <paramref name="host"/> (used by probes, webhooks and the TCP checker).</summary>
    public static async Task<Socket> ConnectAsync(string host, int port, bool allowPrivate, CancellationToken ct)
    {
        var addresses = await ResolveAsync(host, allowPrivate, ct);
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
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback(Func<bool> allowPrivate) =>
        async (context, ct) =>
        {
            var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowPrivate(), ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
}
