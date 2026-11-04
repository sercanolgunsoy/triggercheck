using System.Buffers;
using System.Diagnostics;
using System.Text;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Checks;

public static class MonitoringHttpClients
{
    /// <summary>Probe client that follows up to 5 redirects (used when no 3xx code is expected).</summary>
    public const string Probe = "TriggerCheck.Probe";
    /// <summary>Probe client that does not follow redirects (used when ExpectedStatusCodes accepts a 3xx code).</summary>
    public const string ProbeNoRedirect = "TriggerCheck.Probe.NoRedirect";
    /// <summary>Client for webhook alert delivery.</summary>
    public const string Webhook = "TriggerCheck.Webhook";

    public const string UserAgent = "TriggerCheck/1.0";
    public const int MaxRedirects = 5;
}

/// <summary>
/// Shared HTTP probe. Redirect policy: redirects are followed (max 5) unless the monitor's ExpectedStatusCodes
/// accepts a 3xx code, in which case the first response is evaluated as-is so e.g. "301" can be asserted.
/// Timeout covers the whole probe (headers + capped body read for Keyword / JsonApi).
/// </summary>
public abstract class HttpCheckerBase(IHttpClientFactory httpClientFactory) : IMonitorChecker
{
    /// <summary>Maximum number of response body bytes inspected for keyword matching / JSON assertions.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    public abstract MonitorType Type { get; }

    /// <summary>True when the body must be read and passed to <see cref="CheckBody"/>.</summary>
    protected virtual bool ReadsBody => false;

    /// <summary>Type specific configuration error (checked before any request), or null.</summary>
    protected virtual string? ValidateConfig(MonitorEntity monitor) => null;

    /// <summary>Adds type specific headers to the probe request.</summary>
    protected virtual void Prepare(HttpRequestMessage request) { }

    /// <summary>
    /// Evaluates the (capped) response body; returns the customer-visible failure reason or null when it passes.
    /// <paramref name="truncated"/> is true when the body was longer than <see cref="MaxBodyBytes"/>.
    /// </summary>
    protected virtual string? CheckBody(MonitorEntity monitor, string body, bool truncated) => null;

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        if (!StatusCodeMatcher.TryParse(monitor.ExpectedStatusCodes, out var matcher, out var specError))
            return CheckOutcome.Down("Invalid expected status codes: " + specError);

        var target = monitor.Target?.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return CheckOutcome.Down($"Invalid URL '{target}' (must be absolute http:// or https://)");

        if (ValidateConfig(monitor) is { } configError)
            return CheckOutcome.Down(configError);

        HttpMethod method;
        try { method = new HttpMethod(string.IsNullOrWhiteSpace(monitor.HttpMethod) ? "GET" : monitor.HttpMethod.Trim().ToUpperInvariant()); }
        catch (FormatException) { return CheckOutcome.Down($"Invalid HTTP method '{monitor.HttpMethod}'"); }

        var timeout = CheckTimeouts.For(monitor);
        var client = httpClientFactory.CreateClient(matcher.AcceptsRedirectStatus ? MonitoringHttpClients.ProbeNoRedirect : MonitoringHttpClients.Probe);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var probe = new ProbeContext(uri.IdnHost);
        int? ttfb = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Options.Set(ProbeContext.Key, probe);
            Prepare(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            ttfb = Elapsed(sw);
            var code = (int)response.StatusCode;

            if (!matcher.IsMatch(code))
                return WithProbe(CheckOutcome.Down($"Unexpected status code {code} (expected {matcher})", Elapsed(sw), code), probe, ttfb);

            if (ReadsBody)
            {
                var (body, truncated) = await ReadCappedBodyAsync(response, cts.Token);
                var ms = Elapsed(sw);
                return WithProbe(CheckBody(monitor, body, truncated) is { } bodyError
                    ? CheckOutcome.Down(bodyError, ms, code)
                    : CheckOutcome.Up(ms, code), probe, ttfb);
            }

            return WithProbe(CheckOutcome.Up(Elapsed(sw), code), probe, ttfb);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A rejected certificate gets a precise, non-secret reason (expired / name mismatch / untrusted chain).
            var error = probe.Certificate?.Error is { } certError && ttfb is null
                ? "TLS certificate error: " + certError
                : ProbeErrors.Describe(ex, timeout);
            return WithProbe(CheckOutcome.Down(error, Elapsed(sw)), probe, ttfb);
        }
    }

    private static CheckOutcome WithProbe(CheckOutcome outcome, ProbeContext probe, int? ttfb) =>
        outcome with { Timings = probe.Timings(ttfb), Certificate = probe.Certificate };

    private static int Elapsed(Stopwatch sw) => (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

    private static async Task<(string Body, bool Truncated)> ReadCappedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // One extra byte tells whether the body was longer than the cap.
        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes + 1);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var total = 0;
            while (total <= MaxBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, MaxBodyBytes + 1 - total), ct);
                if (read == 0) break;
                total += read;
            }
            var truncated = total > MaxBodyBytes;
            return (ResolveEncoding(response).GetString(buffer, 0, Math.Min(total, MaxBodyBytes)), truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* unknown charset -> UTF-8 */ }
        }
        return Encoding.UTF8;
    }
}

public sealed class HttpMonitorChecker(IHttpClientFactory httpClientFactory) : HttpCheckerBase(httpClientFactory)
{
    public override MonitorType Type => MonitorType.Http;
}

public sealed class KeywordMonitorChecker(IHttpClientFactory httpClientFactory) : HttpCheckerBase(httpClientFactory)
{
    public override MonitorType Type => MonitorType.Keyword;
    protected override bool ReadsBody => true;

    protected override string? ValidateConfig(MonitorEntity monitor) =>
        string.IsNullOrEmpty(monitor.Keyword) ? "Keyword is not configured" : null;

    protected override string? CheckBody(MonitorEntity monitor, string body, bool truncated) =>
        body.Contains(monitor.Keyword!, StringComparison.OrdinalIgnoreCase) ? null : $"Keyword '{monitor.Keyword}' not found in response";
}

/// <summary>
/// HTTP(S) probe (same guarded client, redirect policy, timings and TLS capture as Http) plus a JSON path assertion on
/// the response body (see <see cref="JsonAssertion"/>). Bodies over <see cref="HttpCheckerBase.MaxBodyBytes"/> fail.
/// </summary>
public sealed class JsonApiMonitorChecker(IHttpClientFactory httpClientFactory) : HttpCheckerBase(httpClientFactory)
{
    public override MonitorType Type => MonitorType.JsonApi;
    protected override bool ReadsBody => true;

    protected override string? ValidateConfig(MonitorEntity monitor) => JsonAssertion.ValidateConfig(monitor.JsonPath, monitor.AssertionOperator, monitor.ExpectedValue);

    protected override void Prepare(HttpRequestMessage request) =>
        request.Headers.Accept.ParseAdd("application/json");

    protected override string? CheckBody(MonitorEntity monitor, string body, bool truncated) =>
        truncated
            ? $"Response is larger than {MaxBodyBytes / (1024 * 1024)} MB"
            : JsonAssertion.Evaluate(body, monitor.JsonPath!, monitor.AssertionOperator!.Value, monitor.ExpectedValue);
}

internal static class CheckTimeouts
{
    public static TimeSpan For(MonitorEntity m) => TimeSpan.FromSeconds(Math.Clamp(m.TimeoutSeconds <= 0 ? 10 : m.TimeoutSeconds, 1, 120));

    /// <summary>Host name/IP for Tcp/Ping targets; tolerates a pasted URL ("https://host/path" -> "host").</summary>
    public static string? NormalizeHost(string? target)
    {
        var t = target?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Contains("://") && Uri.TryCreate(t, UriKind.Absolute, out var uri)) return uri.IdnHost;
        return t;
    }
}
