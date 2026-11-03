using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>Up when a TCP connection to Target:Port can be established within the timeout.</summary>
public sealed class TcpMonitorChecker : IMonitorChecker
{
    public MonitorType Type => MonitorType.Tcp;

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        var host = CheckTimeouts.NormalizeHost(monitor.Target);
        if (host is null) return CheckOutcome.Down("Host is not configured");
        if (monitor.Port is not (>= 1 and <= 65535)) return CheckOutcome.Down("Port is not configured (1-65535)");

        var timeout = CheckTimeouts.For(monitor);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            // Dual-mode socket: works for IPv4 and IPv6 targets.
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(host, monitor.Port.Value, cts.Token);
            var ms = (int)sw.ElapsedMilliseconds;
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { /* peer may already be gone */ }
            return CheckOutcome.Up(ms);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckOutcome.Down(ProbeErrors.Describe(ex, timeout), (int)sw.ElapsedMilliseconds);
        }
    }
}

/// <summary>
/// ICMP echo via <see cref="Ping"/>. On Linux/macOS without raw-socket privileges .NET falls back to the system
/// <c>ping</c> utility; in containers ICMP may be blocked entirely (the monitor will then report Down).
/// </summary>
public sealed class PingMonitorChecker : IMonitorChecker
{
    private static readonly byte[] Payload = new byte[32];

    public MonitorType Type => MonitorType.Ping;

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        var host = CheckTimeouts.NormalizeHost(monitor.Target);
        if (host is null) return CheckOutcome.Down("Host is not configured");

        var timeout = CheckTimeouts.For(monitor);
        var sw = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout, Payload, null, ct);
            var ms = (int)sw.ElapsedMilliseconds;
            if (reply.Status == IPStatus.Success)
                return CheckOutcome.Up(reply.RoundtripTime > 0 ? (int)reply.RoundtripTime : ms);
            return CheckOutcome.Down(reply.Status == IPStatus.TimedOut ? $"Timeout after {timeout.TotalSeconds:0.#}s" : $"Ping failed: {reply.Status}", ms);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckOutcome.Down(ProbeErrors.Describe(ex, timeout), (int)sw.ElapsedMilliseconds);
        }
    }
}
