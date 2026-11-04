using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Checks.Dns;

/// <summary>One answer record of the queried type. MX: <see cref="Value"/> is the exchange host (preference dropped).</summary>
public sealed record DnsRecord(DnsRecordType Type, string Value);

/// <summary>Decoded response: RCODE (0 = NOERROR, 3 = NXDOMAIN...), the matching answer records and the TC flag.</summary>
public sealed record DnsAnswer(int ResponseCode, IReadOnlyList<DnsRecord> Records, bool Truncated);

/// <summary>The resolver sent something that is not a valid response to our query.</summary>
public sealed class DnsFormatException(string message) : Exception(message);

/// <summary>
/// Minimal RFC 1035 message codec: a single-question recursive query (with an EDNS0 OPT record advertising a 1232
/// byte UDP payload) and a defensive decoder for the answer section. Every read is bounds-checked, compression pointers
/// may only point backwards and are limited in number, so a hostile or broken response raises
/// <see cref="DnsFormatException"/> instead of looping or reading out of range.
/// </summary>
public static class DnsMessage
{
    public const int HeaderLength = 12;
    public const ushort EdnsUdpPayload = 1232;
    private const ushort ClassIn = 1;
    private const ushort TypeOpt = 41;
    private const int MaxNameLength = 255;
    private const int MaxPointerJumps = 64;

    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Lower-case ASCII (punycode) form of a DNS name without the trailing dot; throws <see cref="ArgumentException"/>
    /// when invalid. Labels may contain letters, digits, '-' and '_' (service / policy names such as _dmarc.example.com),
    /// must not start or end with '-', and IDN names are converted with <see cref="IdnMapping"/>. IP literals are rejected.
    /// </summary>
    public static string NormalizeName(string name)
    {
        var n = name.Trim().TrimEnd('.');
        if (n.Length == 0) throw new ArgumentException("Host name is empty");
        if (System.Net.IPAddress.TryParse(n.Trim('[', ']'), out _)) throw new ArgumentException("An IP address is not a DNS name");
        string ascii;
        try { ascii = Idn.GetAscii(n); }
        catch (ArgumentException) { throw new ArgumentException("Invalid host name"); }
        if (ascii.Length > 253) throw new ArgumentException("Invalid host name");
        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new ArgumentException("Invalid host name");
        }
        return ascii.ToLowerInvariant();
    }

    public static byte[] EncodeQuery(ushort id, string name, DnsRecordType type)
    {
        var labels = NormalizeName(name).Split('.');
        var buffer = new List<byte>(HeaderLength + name.Length + 2 + 4 + 11);
        void U16(int v) { buffer.Add((byte)(v >> 8)); buffer.Add((byte)v); }

        U16(id);
        U16(0x0100);  // standard query, RD (recursion desired)
        U16(1);       // QDCOUNT
        U16(0);       // ANCOUNT
        U16(0);       // NSCOUNT
        U16(1);       // ARCOUNT: EDNS0 OPT
        foreach (var label in labels)
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }
        buffer.Add(0);
        U16((ushort)type);
        U16(ClassIn);
        // OPT pseudo-record (RFC 6891): root name, type 41, class = UDP payload size, TTL 0, no options.
        buffer.Add(0);
        U16(TypeOpt);
        U16(EdnsUdpPayload);
        U16(0);
        U16(0);
        U16(0);
        return buffer.ToArray();
    }

    /// <summary>True when <paramref name="message"/> looks like the response to query <paramref name="id"/> (ID + QR bit).</summary>
    public static bool IsResponseTo(ReadOnlySpan<byte> message, ushort id) =>
        message.Length >= HeaderLength && BinaryPrimitives.ReadUInt16BigEndian(message) == id && (message[2] & 0x80) != 0;

    public static DnsAnswer Decode(ReadOnlySpan<byte> message, ushort id, string name, DnsRecordType type)
    {
        if (!IsResponseTo(message, id)) throw new DnsFormatException("response does not match the query");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var truncated = (flags & 0x0200) != 0;
        var rcode = flags & 0x000F;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);

        var pos = HeaderLength;
        var expectedName = NormalizeName(name);
        for (var q = 0; q < qd; q++)
        {
            var qname = ReadName(message, ref pos);
            var qtype = ReadU16(message, ref pos);
            ReadU16(message, ref pos); // class
            if (q == 0 && (!string.Equals(qname, expectedName, StringComparison.OrdinalIgnoreCase) || qtype != (ushort)type))
                throw new DnsFormatException("response does not match the query");
        }
        if (qd == 0 && rcode == 0) throw new DnsFormatException("response has no question section");

        // A truncated (UDP) answer may stop anywhere; the caller repeats the query over TCP.
        if (truncated) return new DnsAnswer(rcode, [], true);

        var records = new List<DnsRecord>();
        for (var a = 0; a < an; a++)
        {
            ReadName(message, ref pos);
            var rtype = ReadU16(message, ref pos);
            var rclass = ReadU16(message, ref pos);
            pos += 4; // TTL
            var rdLength = ReadU16(message, ref pos);
            if (pos > message.Length || rdLength > message.Length - pos) throw new DnsFormatException("record data out of range");
            var rdStart = pos;
            pos += rdLength;
            if (rclass != ClassIn || rtype != (ushort)type) continue; // e.g. the CNAME chain in front of an A answer
            records.Add(new DnsRecord(type, DecodeData(message, rdStart, rdLength, type)));
        }
        return new DnsAnswer(rcode, records, truncated);
    }

    private static string DecodeData(ReadOnlySpan<byte> message, int start, int length, DnsRecordType type)
    {
        var data = message.Slice(start, length);
        var p = start;
        switch (type)
        {
            case DnsRecordType.A:
                if (length != 4) throw new DnsFormatException("invalid A record");
                return new IPAddress(data).ToString();
            case DnsRecordType.AAAA:
                if (length != 16) throw new DnsFormatException("invalid AAAA record");
                return new IPAddress(data).ToString();
            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
                var target = ReadName(message, ref p);
                if (p > start + length) throw new DnsFormatException("record data out of range");
                return target;
            case DnsRecordType.MX:
                if (length < 3) throw new DnsFormatException("invalid MX record");
                p += 2; // preference
                var exchange = ReadName(message, ref p);
                if (p > start + length) throw new DnsFormatException("record data out of range");
                return exchange;
            case DnsRecordType.TXT:
                var sb = new StringBuilder();
                var i = 0;
                while (i < data.Length)
                {
                    var len = data[i++];
                    if (len > data.Length - i) throw new DnsFormatException("invalid TXT record");
                    sb.Append(Encoding.UTF8.GetString(data.Slice(i, len)));
                    i += len;
                }
                return sb.ToString();
            default:
                throw new DnsFormatException("unsupported record type");
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> message, ref int pos)
    {
        if (pos < 0 || pos + 2 > message.Length) throw new DnsFormatException("message is truncated");
        var v = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
        pos += 2;
        return v;
    }

    /// <summary>Reads a (possibly compressed) name; <paramref name="pos"/> ends after the name's in-place bytes.</summary>
    public static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var sb = new StringBuilder();
        var p = pos;
        var jumps = 0;
        var end = -1;
        var length = 0;
        while (true)
        {
            if (p < 0 || p >= message.Length) throw new DnsFormatException("name out of range");
            var len = message[p];
            if (len == 0)
            {
                p++;
                break;
            }
            switch (len & 0xC0)
            {
                case 0xC0:
                    if (p + 1 >= message.Length) throw new DnsFormatException("name out of range");
                    var target = ((len & 0x3F) << 8) | message[p + 1];
                    if (end < 0) end = p + 2;
                    // Pointers must go backwards: forward pointers are never needed and could form loops.
                    if (target >= p || ++jumps > MaxPointerJumps) throw new DnsFormatException("invalid name compression");
                    p = target;
                    continue;
                case 0x00:
                    if (p + 1 + len > message.Length) throw new DnsFormatException("name out of range");
                    length += len + 1;
                    if (length > MaxNameLength) throw new DnsFormatException("name too long");
                    if (sb.Length > 0) sb.Append('.');
                    sb.Append(Encoding.ASCII.GetString(message.Slice(p + 1, len)));
                    p += 1 + len;
                    continue;
                default:
                    throw new DnsFormatException("invalid label type");
            }
        }
        pos = end >= 0 ? end : p;
        return sb.ToString();
    }
}

/// <summary>
/// DNS over UDP with TCP fallback (RFC 1035 4.2 / RFC 7766) against one resolver. Resolver addresses are operator
/// configuration (Monitoring:DnsResolvers), not customer input, so they are not subject to TargetGuard; the queried
/// name is customer input but is only sent as data, never connected to.
/// </summary>
public static class DnsWireClient
{
    private const int MaxUdpResponse = 4096;

    public static async Task<DnsAnswer> QueryAsync(IPEndPoint server, string name, DnsRecordType type, CancellationToken ct)
    {
        var id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        var query = DnsMessage.EncodeQuery(id, name, type);
        var answer = await QueryUdpAsync(server, query, id, name, type, ct);
        return answer.Truncated ? await QueryTcpAsync(server, query, id, name, type, ct) : answer;
    }

    private static async Task<DnsAnswer> QueryUdpAsync(IPEndPoint server, byte[] query, ushort id, string name, DnsRecordType type, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, ct); // connected: datagrams from other sources are dropped by the OS
        await socket.SendAsync(query, SocketFlags.None, ct);
        var buffer = new byte[MaxUdpResponse];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            // Stray / late datagrams for an earlier ID are ignored; the timeout bounds the wait.
            if (!DnsMessage.IsResponseTo(buffer.AsSpan(0, read), id)) continue;
            return DnsMessage.Decode(buffer.AsSpan(0, read), id, name, type);
        }
    }

    private static async Task<DnsAnswer> QueryTcpAsync(IPEndPoint server, byte[] query, ushort id, string name, DnsRecordType type, CancellationToken ct)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(server, ct);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct);

        var lengthPrefix = new byte[2];
        await stream.ReadExactlyAsync(lengthPrefix, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
        if (length < DnsMessage.HeaderLength) throw new DnsFormatException("message is truncated");
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, ct);
        var answer = DnsMessage.Decode(message, id, name, type);
        // TCP has no size limit, so TC set here means a broken or hostile server: never loop or trust a partial answer.
        if (answer.Truncated) throw new DnsFormatException("truncated response over TCP");
        return answer;
    }
}
