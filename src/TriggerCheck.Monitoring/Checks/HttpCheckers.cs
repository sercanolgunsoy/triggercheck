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
/// Timeout covers the whole probe (headers + capped body read for Keyword).
/// </summary>
public abstract class HttpCheckerBase(IHttpClientFactory httpClientFactory) : IMonitorChecker
{
    /// <summary>Maximum number of response body bytes inspected for keyword matching.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    public abstract MonitorType Type { get; }
    protected abstract bool MatchKeyword { get; }

    public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        if (!StatusCodeMatcher.TryParse(monitor.ExpectedStatusCodes, out var matcher, out var specError))
            return CheckOutcome.Down("Invalid expected status codes: " + specError);

        var target = monitor.Target?.Trim();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return CheckOutcome.Down($"Invalid URL '{target}' (must be absolute http:// or https://)");

        var keyword = monitor.Keyword;
        if (MatchKeyword && string.IsNullOrEmpty(keyword))
            return CheckOutcome.Down("Keyword is not configured");

        HttpMethod method;
        try { method = new HttpMethod(string.IsNullOrWhiteSpace(monitor.HttpMethod) ? "GET" : monitor.HttpMethod.Trim().ToUpperInvariant()); }
        catch (FormatException) { return CheckOutcome.Down($"Invalid HTTP method '{monitor.HttpMethod}'"); }

        var timeout = CheckTimeouts.For(monitor);
        var client = httpClientFactory.CreateClient(matcher.AcceptsRedirectStatus ? MonitoringHttpClients.ProbeNoRedirect : MonitoringHttpClients.Probe);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)response.StatusCode;

            if (!matcher.IsMatch(code))
                return CheckOutcome.Down($"Unexpected status code {code} (expected {matcher})", Elapsed(sw), code);

            if (MatchKeyword)
            {
                var body = await ReadCappedBodyAsync(response, cts.Token);
                var ms = Elapsed(sw);
                return body.Contains(keyword!, StringComparison.OrdinalIgnoreCase)
                    ? CheckOutcome.Up(ms, code)
                    : CheckOutcome.Down($"Keyword '{keyword}' not found in response", ms, code);
            }

            return CheckOutcome.Up(Elapsed(sw), code);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return CheckOutcome.Down(ProbeErrors.Describe(ex, timeout), Elapsed(sw));
        }
    }

    private static int Elapsed(Stopwatch sw) => (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

    private static async Task<string> ReadCappedBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var total = 0;
            while (total < MaxBodyBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, MaxBodyBytes - total), ct);
                if (read == 0) break;
                total += read;
            }
            return ResolveEncoding(response).GetString(buffer, 0, total);
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
    protected override bool MatchKeyword => false;
}

public sealed class KeywordMonitorChecker(IHttpClientFactory httpClientFactory) : HttpCheckerBase(httpClientFactory)
{
    public override MonitorType Type => MonitorType.Keyword;
    protected override bool MatchKeyword => true;
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
