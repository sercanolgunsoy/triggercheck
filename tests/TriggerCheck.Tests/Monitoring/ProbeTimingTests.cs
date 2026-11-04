using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>ENG-8 phase timings and FEAT-1 certificate capture through the real guarded probe handler.</summary>
public class ProbeTimingTests
{
    private static IMonitorChecker Checker(MonitoringTestHost host, MonitorType type) =>
        host.Services.GetServices<IMonitorChecker>().Single(c => c.Type == type);

    [Fact]
    public async Task New_connection_has_dns_connect_and_ttfb_and_a_reused_one_has_only_ttfb()
    {
        await using var server = await LocalHttpServer.StartAsync(app => app.MapGet("/ok", async ctx =>
        {
            await ctx.Response.Body.FlushAsync(); // headers first
            await Task.Delay(30);
            await ctx.Response.WriteAsync("hello world");
        }));
        await using var host = await MonitoringTestHost.CreateAsync();
        var monitor = new MonitorEntity { Type = MonitorType.Keyword, Target = server.Url("/ok"), Keyword = "world", TimeoutSeconds = 10 };

        var first = await Checker(host, MonitorType.Keyword).CheckAsync(monitor, CancellationToken.None);
        Assert.True(first.IsUp, first.Error);
        var t = first.Timings!;
        Assert.NotNull(t.DnsMs);
        Assert.NotNull(t.ConnectMs);
        Assert.Null(t.TlsMs); // plain http
        Assert.NotNull(t.TtfbMs);
        Assert.False(t.ReusedConnection);
        Assert.True(t.TtfbMs >= t.DnsMs + t.ConnectMs);
        Assert.True(first.ResponseTimeMs >= t.TtfbMs);
        Assert.True(first.ResponseTimeMs - t.TtfbMs >= 20, $"body transfer should include the server delay: total {first.ResponseTimeMs}, ttfb {t.TtfbMs}");
        Assert.Null(first.Certificate);

        var second = await Checker(host, MonitorType.Keyword).CheckAsync(monitor, CancellationToken.None);
        Assert.True(second.IsUp, second.Error);
        Assert.True(second.Timings!.ReusedConnection);
        Assert.Null(second.Timings.DnsMs);
        Assert.Null(second.Timings.ConnectMs);
        Assert.Null(second.Timings.TlsMs);
        Assert.NotNull(second.Timings.TtfbMs);
    }

    [Fact]
    public async Task Runner_stores_the_phases_on_the_check_result()
    {
        await using var server = await LocalHttpServer.StartAsync(app => app.MapGet("/", () => "ok"));
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.Target = server.Url("/"));

        var r = await host.RunCheckAsync(monitor.Id);
        Assert.True(r.IsUp, r.Error);
        var stored = await host.WithDbAsync(db => db.CheckResults.SingleAsync());
        Assert.NotNull(stored.DnsMs);
        Assert.NotNull(stored.ConnectMs);
        Assert.Null(stored.TlsMs);
        Assert.NotNull(stored.TtfbMs);
    }

    [Fact]
    public async Task Connection_failure_keeps_dns_but_has_no_ttfb()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var port = FreePort();
        var outcome = await Checker(host, MonitorType.Http).CheckAsync(
            new MonitorEntity { Type = MonitorType.Http, Target = $"http://127.0.0.1:{port}/", TimeoutSeconds = 5 }, CancellationToken.None);
        Assert.False(outcome.IsUp);
        Assert.Equal("Connection refused", outcome.Error);
        Assert.Null(outcome.Timings!.TtfbMs);
    }

    [Fact]
    public async Task Untrusted_https_certificate_fails_the_check_with_a_clear_error_and_is_captured()
    {
        using var cert = SelfSignedCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        await using var server = await HttpsServer.StartAsync(cert);
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.Target = server.Url);

        var r = await host.RunCheckAsync(monitor.Id);

        Assert.False(r.IsUp);
        Assert.StartsWith("TLS certificate error: ", r.Error);
        Assert.Contains("untrusted root", r.Error);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(cert.NotAfter.ToUniversalTime(), m.CertExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Contains("CN=probe.test", m.CertSubject);
        Assert.Contains("CN=probe.test", m.CertIssuer);
        Assert.Contains("untrusted root", m.CertError);
        Assert.Equal(host.Now, m.CertCheckedAtUtc);
    }

    [Fact]
    public async Task Expired_and_mismatched_certificates_are_described()
    {
        using var cert = SelfSignedCertificate(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-2));
        await using var server = await HttpsServer.StartAsync(cert);
        await using var host = await MonitoringTestHost.CreateAsync();
        // "localhost" is not in the certificate (only probe.test and 127.0.0.1).
        var outcome = await Checker(host, MonitorType.Http).CheckAsync(
            new MonitorEntity { Type = MonitorType.Http, Target = server.Url.Replace("127.0.0.1", "localhost"), TimeoutSeconds = 5 }, CancellationToken.None);

        Assert.False(outcome.IsUp);
        Assert.Contains("certificate expired on", outcome.Error);
        Assert.Contains("name does not match", outcome.Error);
        Assert.NotNull(outcome.Certificate);
    }

    [Fact]
    public void DescribeErrors_is_readable_and_null_for_valid_certificates()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(ProbeConnection.DescribeErrors(SslPolicyErrors.None, null, now.AddDays(-1), now.AddDays(1), now));
        Assert.Equal("certificate expired on 2026-05-30",
            ProbeConnection.DescribeErrors(SslPolicyErrors.RemoteCertificateChainErrors, null, now.AddDays(-90), now.AddDays(-2), now));
        Assert.Equal("certificate name does not match the host",
            ProbeConnection.DescribeErrors(SslPolicyErrors.RemoteCertificateNameMismatch, null, now.AddDays(-1), now.AddDays(1), now));
        Assert.Equal("certificate chain error",
            ProbeConnection.DescribeErrors(SslPolicyErrors.RemoteCertificateChainErrors, null, now.AddDays(-1), now.AddDays(1), now));
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static X509Certificate2 SelfSignedCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=probe.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("probe.test");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(notBefore, notAfter);
        // Re-import so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x");
    }

    private sealed class HttpsServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string Url { get; }

        private HttpsServer(WebApplication app, string url) { _app = app; Url = url; }

        public static async Task<HttpsServer> StartAsync(X509Certificate2 cert)
        {
            var port = FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.UseHttps(cert)));
            var app = builder.Build();
            app.MapGet("/", () => "secure");
            await app.StartAsync();
            return new HttpsServer(app, $"https://127.0.0.1:{port}/");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
