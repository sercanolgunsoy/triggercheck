using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TriggerCheck.Monitoring.Security;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>Server certificate seen during a TLS handshake. <see cref="Error"/> is null when the certificate is valid.</summary>
public sealed record CertificateInfo(DateTime NotAfterUtc, string Subject, string Issuer, string? Error);

/// <summary>
/// Phase breakdown of one HTTP probe in milliseconds. <see cref="DnsMs"/>/<see cref="ConnectMs"/>/<see cref="TlsMs"/>
/// are null when the probe reused a pooled connection (no new connection was opened); <see cref="TlsMs"/> is also
/// null for plain http. When a probe opened several connections (redirect hops) the phases are summed.
/// <see cref="TtfbMs"/> is measured from the start of the probe to the response headers, so it includes the
/// connection phases; it is null when no response arrived.
/// </summary>
public sealed record ProbeTimings(int? DnsMs, int? ConnectMs, int? TlsMs, int? TtfbMs)
{
    public bool ReusedConnection => DnsMs is null && TtfbMs is not null;
}

/// <summary>
/// Per-request state shared between the HTTP checker and <see cref="ProbeConnection.ConnectCallback"/>, carried in
/// <see cref="HttpRequestMessage.Options"/> (no AsyncLocal). The connect callback sees it through
/// <see cref="SocketsHttpConnectionContext.InitialRequestMessage"/>. Caveat: with HTTP/1.1 pooling a connection opened
/// for one request can occasionally be handed to another waiting request; the phases are then attributed to the
/// request that triggered the connection. Probes rarely share a host concurrently, so this is accepted.
/// </summary>
public sealed class ProbeContext(string host)
{
    public static readonly HttpRequestOptionsKey<ProbeContext> Key = new("TriggerCheck.ProbeContext");

    private readonly object _lock = new();
    private long? _dnsTicks, _connectTicks, _tlsTicks;
    private CertificateInfo? _certificate;

    /// <summary>Host of the monitored URL; only certificates presented for this host are recorded.</summary>
    public string Host { get; } = ProbeConnection.BareHost(host);

    public CertificateInfo? Certificate { get { lock (_lock) return _certificate; } }

    internal void AddConnection(long dnsTicks, long connectTicks, long? tlsTicks)
    {
        lock (_lock)
        {
            _dnsTicks = (_dnsTicks ?? 0) + dnsTicks;
            _connectTicks = (_connectTicks ?? 0) + connectTicks;
            if (tlsTicks is { } t) _tlsTicks = (_tlsTicks ?? 0) + t;
        }
    }

    internal void SetCertificate(string host, CertificateInfo info)
    {
        if (!string.Equals(ProbeConnection.BareHost(host), Host, StringComparison.OrdinalIgnoreCase)) return; // a redirect hop to another host
        lock (_lock) _certificate ??= info;
    }

    public ProbeTimings Timings(int? ttfbMs)
    {
        lock (_lock) return new ProbeTimings(Ms(_dnsTicks), Ms(_connectTicks), Ms(_tlsTicks), ttfbMs);
    }

    private static int? Ms(long? ticks) =>
        ticks is { } t ? (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(0, t).TotalMilliseconds) : null;
}

/// <summary>
/// Connect callback of the probe handlers. Same SSRF guarantees as <see cref="TargetGuard.ConnectCallback"/> (DNS is
/// resolved and validated by <see cref="TargetGuard.ResolveAsync"/> before any socket is opened, for every hop), plus:
/// <list type="bullet">
/// <item>DNS and TCP connect are timed separately into the request's <see cref="ProbeContext"/>;</item>
/// <item>for https the TLS handshake is done here (SocketsHttpHandler skips its own handshake when the callback
/// returns an <see cref="SslStream"/>), so it can be timed exactly and the server certificate captured. Validation
/// is the handler's default: any <see cref="SslPolicyErrors"/> fails the connection, revocation is not checked.
/// Only HTTP/1.1 is negotiated (ALPN), which is what the probe requests anyway. TLS is only done here when the
/// request is https and its host/port match the endpoint being connected; otherwise the plain stream is returned
/// and the handler does its own handshake (no timing/certificate then).</item>
/// </list>
/// </summary>
public static class ProbeConnection
{
    /// <param name="trustOverride">
    /// Tests only: may accept a certificate the OS rejects (e.g. a self-signed test certificate). Never set in production;
    /// the certificate is then recorded as valid.
    /// </param>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback(
        Func<bool> allowPrivate, ILogger? logger = null, RemoteCertificateValidationCallback? trustOverride = null) =>
        async (context, ct) =>
        {
            var host = BareHost(context.DnsEndPoint.Host);
            context.InitialRequestMessage.Options.TryGetValue(ProbeContext.Key, out var probe);

            var start = Stopwatch.GetTimestamp();
            var addresses = await TargetGuard.ResolveAsync(host, allowPrivate(), ct, logger);
            var resolved = Stopwatch.GetTimestamp();
            var socket = await TargetGuard.ConnectResolvedAsync(addresses, context.DnsEndPoint.Port, ct);
            var connected = Stopwatch.GetTimestamp();
            var stream = new NetworkStream(socket, ownsSocket: true);

            var uri = context.InitialRequestMessage.RequestUri;
            var ownTls = uri is not null && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(BareHost(uri.IdnHost), host, StringComparison.OrdinalIgnoreCase) && uri.Port == context.DnsEndPoint.Port;
            if (!ownTls)
            {
                probe?.AddConnection(resolved - start, connected - resolved, null);
                return stream;
            }

            CertificateInfo? certificate = null;
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        if (errors != SslPolicyErrors.None && trustOverride?.Invoke(sender, cert, chain, errors) == true)
                            errors = SslPolicyErrors.None;
                        // Wall clock on purpose: the OS validated the chain against it, so the message must agree.
                        certificate = Describe(cert, chain, errors, DateTime.UtcNow);
                        return errors == SslPolicyErrors.None;
                    }
                }, ct);
            }
            catch
            {
                if (certificate is not null) probe?.SetCertificate(host, certificate);
                await ssl.DisposeAsync();
                throw;
            }
            probe?.AddConnection(resolved - start, connected - resolved, Stopwatch.GetTimestamp() - connected);
            if (certificate is not null) probe?.SetCertificate(host, certificate);
            return ssl;
        };

    /// <summary>Host without IPv6 brackets ("[::1]" -> "::1"), as used for SNI/validation and host comparisons.</summary>
    public static string BareHost(string host) =>
        host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>Summarizes a server certificate and its validation result. The error text is customer-visible and contains no secrets.</summary>
    public static CertificateInfo? Describe(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, DateTime nowUtc)
    {
        if (certificate is null) return null;
        var owned = certificate as X509Certificate2 is null;
        var cert = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        try
        {
            var notAfter = cert.NotAfter.ToUniversalTime();
            return new CertificateInfo(notAfter, Trim(cert.Subject), Trim(cert.Issuer), DescribeErrors(errors, chain, cert.NotBefore.ToUniversalTime(), notAfter, nowUtc));
        }
        finally
        {
            if (owned) cert.Dispose();
        }
    }

    public static string? DescribeErrors(SslPolicyErrors errors, X509Chain? chain, DateTime notBeforeUtc, DateTime notAfterUtc, DateTime nowUtc)
    {
        if (errors == SslPolicyErrors.None) return null;
        var problems = new List<string>();
        if (notAfterUtc <= nowUtc) problems.Add($"certificate expired on {notAfterUtc:yyyy-MM-dd}");
        else if (notBeforeUtc > nowUtc) problems.Add($"certificate is not valid before {notBeforeUtc:yyyy-MM-dd}");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) problems.Add("certificate name does not match the host");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) problems.Add("no certificate presented");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            var flags = chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (f, s) => f | s.Status) ?? X509ChainStatusFlags.NoError;
            if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot)) problems.Add("untrusted root certificate (self-signed?)");
            if (flags.HasFlag(X509ChainStatusFlags.PartialChain)) problems.Add("incomplete certificate chain");
            if (flags.HasFlag(X509ChainStatusFlags.Revoked)) problems.Add("certificate revoked");
            var known = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.Revoked | X509ChainStatusFlags.NotTimeValid;
            var other = flags & ~known;
            if (other != X509ChainStatusFlags.NoError) problems.Add("certificate chain error: " + other);
            else if (problems.Count == 0) problems.Add("certificate chain error");
        }
        return ProbeErrors.Truncate(string.Join("; ", problems.Distinct()));
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..500];
}
