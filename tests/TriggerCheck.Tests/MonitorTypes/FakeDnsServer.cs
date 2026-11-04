using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks.Dns;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>
/// Minimal DNS server on a random loopback port, UDP and TCP on the same port. <see cref="Handler"/> builds the
/// response for each query (null = no answer, so the client times out).
/// </summary>
public sealed class FakeDnsServer : IAsyncDisposable
{
    private readonly Socket _udp;
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];

    public IPEndPoint EndPoint { get; }
    public Func<byte[], bool, byte[]?> Handler { get; set; } = (_, _) => null;
    public int UdpQueries;
    public int TcpQueries;

    private FakeDnsServer()
    {
        // Bind UDP first, then TCP on the same port (retry on the rare clash with a TCP socket).
        for (var attempt = 0; ; attempt++)
        {
            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)_udp.LocalEndPoint!).Port;
            try
            {
                _tcp = new TcpListener(IPAddress.Loopback, port);
                _tcp.Start();
                EndPoint = new IPEndPoint(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (attempt < 10)
            {
                _udp.Dispose();
            }
        }
    }

    public static FakeDnsServer Start()
    {
        var s = new FakeDnsServer();
        s._loops.Add(Task.Run(s.UdpLoopAsync));
        s._loops.Add(Task.Run(s.TcpLoopAsync));
        return s;
    }

    public string Address => $"127.0.0.1:{EndPoint.Port}";

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                Interlocked.Increment(ref UdpQueries);
                var response = Handler(buffer[..r.ReceivedBytes], false);
                if (response is not null) await _udp.SendToAsync(response, SocketFlags.None, r.RemoteEndPoint, _stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException) { /* client went away */ }
        }
    }

    private async Task TcpLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var len = new byte[2];
                        await stream.ReadExactlyAsync(len, _stop.Token);
                        var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                        await stream.ReadExactlyAsync(query, _stop.Token);
                        Interlocked.Increment(ref TcpQueries);
                        var response = Handler(query, true);
                        if (response is null) return;
                        var framed = new byte[response.Length + 2];
                        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)response.Length);
                        response.CopyTo(framed, 2);
                        await stream.WriteAsync(framed, _stop.Token);
                    }
                    catch (Exception) { /* test client gave up */ }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        _tcp.Stop();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
        _stop.Dispose();
    }

    // ---- message building ----

    /// <summary>Offset of the first byte after the question section of <paramref name="query"/>.</summary>
    public static int QuestionEnd(byte[] query)
    {
        var p = 12;
        while (query[p] != 0) p += query[p] + 1;
        return p + 1 + 4;
    }

    public static DnsRecordType QueryType(byte[] query) => (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(QuestionEnd(query) - 4));

    /// <summary>Response echoing the query's ID and question, with the given answers (built by <see cref="Rr"/>).</summary>
    public static byte[] Response(byte[] query, int rcode = 0, bool truncated = false, params byte[][] answers)
    {
        var msg = new List<byte>();
        msg.AddRange(query[..2]);
        var flags = 0x8180 | rcode | (truncated ? 0x0200 : 0);
        msg.Add((byte)(flags >> 8));
        msg.Add((byte)flags);
        msg.AddRange([0, 1, (byte)(answers.Length >> 8), (byte)answers.Length, 0, 0, 0, 0]);
        msg.AddRange(query[12..QuestionEnd(query)]);
        foreach (var a in answers) msg.AddRange(a);
        return msg.ToArray();
    }

    /// <summary>Answer record whose owner name is a pointer to the question name (offset 12).</summary>
    public static byte[] Rr(DnsRecordType type, byte[] data) => Rr((ushort)type, data);

    public static byte[] Rr(ushort type, byte[] data)
    {
        var r = new List<byte> { 0xC0, 0x0C, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length };
        r.AddRange(data);
        return r.ToArray();
    }

    public static byte[] Name(string name)
    {
        var b = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.Add(0);
        return b.ToArray();
    }

    public static byte[] A(string ip) => IPAddress.Parse(ip).GetAddressBytes();

    public static byte[] Mx(ushort preference, byte[] name) => [(byte)(preference >> 8), (byte)preference, .. name];

    public static byte[] Txt(params string[] parts) =>
        parts.SelectMany(p => new[] { (byte)Encoding.UTF8.GetByteCount(p) }.Concat(Encoding.UTF8.GetBytes(p))).ToArray();
}

public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public static class DnsTestChecker
{
    public static DnsMonitorChecker For(params string[] resolvers) =>
        new(new StaticOptionsMonitor<MonitoringOptions>(new MonitoringOptions { DnsResolvers = resolvers }), NullLogger<DnsMonitorChecker>.Instance);
}
