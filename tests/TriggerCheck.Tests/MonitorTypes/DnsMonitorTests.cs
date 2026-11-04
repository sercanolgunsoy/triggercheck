using System.Net;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Checks.Dns;
using static TriggerCheck.Tests.MonitorTypes.FakeDnsServer;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>DNS monitors against a local fake resolver (UDP + TCP).</summary>
public class DnsMonitorTests : IAsyncLifetime
{
    private FakeDnsServer _dns = null!;

    public Task InitializeAsync()
    {
        _dns = FakeDnsServer.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dns.DisposeAsync();

    private static MonitorEntity Monitor(DnsRecordType type, string target = "example.com", string? expected = null, AssertionOperator? op = null) =>
        new() { Type = MonitorType.Dns, Target = target, DnsRecordType = type, ExpectedValue = expected, AssertionOperator = op, TimeoutSeconds = 2 };

    private Task<CheckOutcome> CheckAsync(MonitorEntity m, params string[]? resolvers) =>
        DnsTestChecker.For(resolvers is { Length: > 0 } ? resolvers : [_dns.Address]).CheckAsync(m, CancellationToken.None);

    [Fact]
    public async Task A_record_up_with_and_without_expected_value()
    {
        _dns.Handler = (q, _) => Response(q, answers: [Rr(DnsRecordType.A, A("93.184.216.34")), Rr(DnsRecordType.A, A("93.184.216.35"))]);

        Assert.True((await CheckAsync(Monitor(DnsRecordType.A))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.A, expected: "93.184.216.35"))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.A, expected: "93.184", op: AssertionOperator.Contains))).IsUp);

        var down = await CheckAsync(Monitor(DnsRecordType.A, expected: "1.2.3.4"));
        Assert.False(down.IsUp);
        Assert.Equal("No A record equals '1.2.3.4' (found: 93.184.216.34, 93.184.216.35)", down.Error);
    }

    [Fact]
    public async Task Query_carries_the_name_and_type_and_ignores_other_answer_types()
    {
        string? askedName = null;
        _dns.Handler = (q, _) =>
        {
            var p = 12;
            askedName = DnsMessage.ReadName(q, ref p);
            // A CNAME chain in front of the AAAA answer, as recursive resolvers return it.
            return Response(q, answers: [Rr(DnsRecordType.CNAME, Name("edge.example.net")), Rr(DnsRecordType.AAAA, IPAddress.Parse("2606:2800::1").GetAddressBytes())]);
        };
        var o = await CheckAsync(Monitor(DnsRecordType.AAAA, target: "WWW.Example.COM.", expected: "2606:2800:0:0::1"));
        Assert.True(o.IsUp, o.Error);
        Assert.Equal("www.example.com", askedName);
    }

    [Fact]
    public async Task Mx_cname_ns_and_txt_records_with_name_compression()
    {
        _dns.Handler = (q, _) => QueryType(q) switch
        {
            // "mail" + pointer to the question name at offset 12: mail.example.com
            DnsRecordType.MX => Response(q, answers: [Rr(DnsRecordType.MX, Mx(10, [4, (byte)'m', (byte)'a', (byte)'i', (byte)'l', 0xC0, 0x0C])), Rr(DnsRecordType.MX, Mx(20, Name("backup.mx.example.net")))]),
            DnsRecordType.CNAME => Response(q, answers: [Rr(DnsRecordType.CNAME, Name("target.cdn.example"))]),
            DnsRecordType.NS => Response(q, answers: [Rr(DnsRecordType.NS, Name("ns1.example.com")), Rr(DnsRecordType.NS, Name("ns2.example.com"))]),
            DnsRecordType.TXT => Response(q, answers: [Rr(DnsRecordType.TXT, Txt("v=spf1 include:_spf.", "example.com ~all")), Rr(DnsRecordType.TXT, Txt("google-site-verification=abc"))]),
            _ => Response(q)
        };

        Assert.True((await CheckAsync(Monitor(DnsRecordType.MX, expected: "mail.example.com."))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.MX, expected: "backup.mx.example.net"))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.CNAME, expected: "TARGET.cdn.example"))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.NS, expected: "ns2.example.com"))).IsUp);
        // TXT character-strings are joined (long SPF records are split into 255-byte chunks).
        Assert.True((await CheckAsync(Monitor(DnsRecordType.TXT, expected: "include:_spf.example.com", op: AssertionOperator.Contains))).IsUp);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.TXT, expected: "google-site-verification=abc"))).IsUp);
        Assert.False((await CheckAsync(Monitor(DnsRecordType.TXT, expected: "v=spf1"))).IsUp); // equals needs the whole record
    }

    [Fact]
    public async Task Nxdomain_and_empty_answers_are_down()
    {
        _dns.Handler = (q, _) => Response(q, rcode: 3);
        var nx = await CheckAsync(Monitor(DnsRecordType.A, target: "nope.example.com"));
        Assert.False(nx.IsUp);
        Assert.Equal("DNS name nope.example.com does not exist (NXDOMAIN)", nx.Error);

        _dns.Handler = (q, _) => Response(q);
        var empty = await CheckAsync(Monitor(DnsRecordType.MX));
        Assert.False(empty.IsUp);
        Assert.Equal("No MX records found for example.com", empty.Error);
    }

    [Fact]
    public async Task Truncated_udp_answer_is_repeated_over_tcp()
    {
        var many = Enumerable.Range(1, 60).Select(i => Rr(DnsRecordType.A, A($"198.51.100.{i}"))).ToArray();
        _dns.Handler = (q, tcp) => tcp ? Response(q, answers: many) : Response(q, truncated: true, answers: many.Take(2).ToArray());

        var o = await CheckAsync(Monitor(DnsRecordType.A, expected: "198.51.100.60"));
        Assert.True(o.IsUp, o.Error);
        Assert.Equal(1, _dns.UdpQueries);
        Assert.Equal(1, _dns.TcpQueries);
    }

    [Fact]
    public async Task Truncated_udp_without_tcp_support_is_down()
    {
        _dns.Handler = (q, tcp) => tcp ? null : Response(q, truncated: true);
        var o = await CheckAsync(Monitor(DnsRecordType.A));
        Assert.False(o.IsUp);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public async Task Malformed_responses_fail_cleanly()
    {
        var cases = new Func<byte[], byte[]>[]
        {
            // compression pointer pointing at itself (loop)
            q => Response(q, answers: [Rr(DnsRecordType.CNAME, [0xC0, 0xFF])]) is var r ? Patch(r, r.Length - 2, (byte)0xC0, (byte)(r.Length - 2)) : r,
            // forward pointer beyond the message
            q => Response(q, answers: [Rr(DnsRecordType.CNAME, [0xC0, 0xF0])]),
            // A record with 3 bytes
            q => Response(q, answers: [Rr(DnsRecordType.A, [1, 2, 3])]),
            // RDLENGTH larger than the message
            q => Response(q, answers: [Rr(DnsRecordType.A, A("1.2.3.4"))]) is var r2 ? Patch(r2, r2.Length - 5, (byte)0x7F) : r2,
            // question for another name
            q => { var r3 = Response(q, answers: [Rr(DnsRecordType.A, A("1.2.3.4"))]); r3[13] = (byte)'X'; return r3; },
            // header only, cut short
            q => Response(q, answers: [Rr(DnsRecordType.A, A("1.2.3.4"))])[..20],
            // TXT length byte past the end
            q => Response(q, answers: [Rr(DnsRecordType.TXT, [50, (byte)'a'])]),
            // reserved label type 0x40
            q => Response(q, answers: [Rr(DnsRecordType.NS, [0x40, 1, 0])]),
        };

        foreach (var make in cases)
        {
            _dns.Handler = (q, _) => make(q);
            // Ask for the record type each case encodes, so the decoder actually parses the broken data.
            var o = await CheckAsync(Monitor(TypeOf(make)));
            Assert.False(o.IsUp);
            Assert.StartsWith("Malformed DNS response", o.Error);
        }
    }

    private static DnsRecordType TypeOf(Func<byte[], byte[]> make)
    {
        var probe = DnsMessage.EncodeQuery(1, "example.com", DnsRecordType.A);
        var response = make(probe);
        // Type of the first answer record (after header + question); falls back to A.
        var p = QuestionEnd(probe) + 2;
        return p + 2 <= response.Length ? (DnsRecordType)((response[p] << 8) | response[p + 1]) : DnsRecordType.A;
    }

    private static byte[] Patch(byte[] message, int offset, params byte[] bytes)
    {
        bytes.CopyTo(message, offset);
        return message;
    }

    [Fact]
    public async Task Responses_with_a_wrong_id_are_ignored_until_timeout()
    {
        _dns.Handler = (q, _) => { var r = Response(q, answers: [Rr(DnsRecordType.A, A("1.2.3.4"))]); r[0] ^= 0xFF; return r; };
        var o = await CheckAsync(Monitor(DnsRecordType.A));
        Assert.False(o.IsUp);
        Assert.Contains("timed out", o.Error);
    }

    [Fact]
    public async Task Next_resolver_is_tried_after_servfail_timeout_or_unreachable()
    {
        await using var second = FakeDnsServer.Start();
        second.Handler = (q, _) => Response(q, answers: [Rr(DnsRecordType.A, A("203.0.113.7"))]);

        _dns.Handler = (q, _) => Response(q, rcode: 2);
        Assert.True((await CheckAsync(Monitor(DnsRecordType.A), _dns.Address, second.Address)).IsUp);

        _dns.Handler = (_, _) => null; // silent
        Assert.True((await CheckAsync(Monitor(DnsRecordType.A), _dns.Address, second.Address)).IsUp);

        var down = await CheckAsync(Monitor(DnsRecordType.A), _dns.Address);
        Assert.False(down.IsUp);
        Assert.Contains("timed out", down.Error);

        _dns.Handler = (q, _) => Response(q, rcode: 5);
        var refused = await CheckAsync(Monitor(DnsRecordType.A), _dns.Address);
        Assert.Equal("DNS query failed: REFUSED", refused.Error);
    }

    [Fact]
    public async Task Nxdomain_is_final_and_not_retried_on_the_next_resolver()
    {
        await using var second = FakeDnsServer.Start();
        second.Handler = (q, _) => Response(q, answers: [Rr(DnsRecordType.A, A("203.0.113.7"))]);
        _dns.Handler = (q, _) => Response(q, rcode: 3);
        Assert.False((await CheckAsync(Monitor(DnsRecordType.A), _dns.Address, second.Address)).IsUp);
        Assert.Equal(0, second.UdpQueries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad..name")]
    [InlineData("-")]
    public async Task Invalid_configuration_is_down_without_a_query(string target)
    {
        var o = await CheckAsync(Monitor(DnsRecordType.A, target: target));
        Assert.False(o.IsUp);
        Assert.Equal(0, _dns.UdpQueries);
    }

    [Fact]
    public async Task Missing_record_type_is_down()
    {
        var o = await CheckAsync(new MonitorEntity { Type = MonitorType.Dns, Target = "example.com", TimeoutSeconds = 2 });
        Assert.Equal("DNS record type is not configured", o.Error);
    }

    [Fact]
    public void Resolver_configuration_parsing()
    {
        Assert.Equal(["1.1.1.1:53", "8.8.8.8:53"], DnsMonitorChecker.ParseResolvers(null).Select(e => e.ToString()));
        Assert.Equal(["1.1.1.1:53", "8.8.8.8:53"], DnsMonitorChecker.ParseResolvers([]).Select(e => e.ToString()));
        Assert.Equal(["9.9.9.9:5353", "[2606:4700::1111]:53", "[2001:db8::1]:5300", "10.0.0.1:53"],
            DnsMonitorChecker.ParseResolvers(["9.9.9.9:5353", "2606:4700::1111", "[2001:db8::1]:5300", "garbage", "10.0.0.1"]).Select(e => e.ToString()));
    }

    [Fact]
    public void Query_encoding_has_rd_flag_edns_and_rejects_bad_names()
    {
        var q = DnsMessage.EncodeQuery(0xBEEF, "Bücher.example", DnsRecordType.TXT);
        Assert.Equal(0xBE, q[0]);
        Assert.Equal(0xEF, q[1]);
        Assert.Equal(0x01, q[2]); // RD
        Assert.Equal(1, q[11]);   // ARCOUNT: OPT
        var p = 12;
        Assert.Equal("xn--bcher-kva.example", DnsMessage.ReadName(q, ref p));
        Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, new string('a', 64) + ".com", DnsRecordType.A));
        Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, string.Join(".", Enumerable.Repeat("abcdefghij", 26)), DnsRecordType.A));
    }
}
