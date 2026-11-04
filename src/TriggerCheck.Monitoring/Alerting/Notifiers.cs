using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Monitoring.Alerting;

public class SmtpOptions
{
    public const string Section = "Smtp";
    /// <summary>Empty = SMTP not configured: emails are only written to the log.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "alerts@triggercheck.local";
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Email via SMTP (<c>Smtp</c> config section). When <see cref="SmtpOptions.Host"/> is empty the message is logged
/// and reported as success with an explanatory note (so the Notifications log still shows the alert was produced).
/// Each send is cancelled after <see cref="SmtpOptions.TimeoutSeconds"/> (SmtpClient.Timeout alone does not apply to
/// SendMailAsync).
/// </summary>
public sealed class EmailNotifier(IOptionsMonitor<SmtpOptions> options, ILogger<EmailNotifier> logger) : INotifier
{
    public const string NotConfiguredNote = "SMTP not configured; email logged only";

    public AlertChannelType Type => AlertChannelType.Email;

    public async Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!MailAddress.TryCreate(target.Address?.Trim(), out var to))
            return NotifyResult.Fail($"Invalid email address '{target.Address}'");

        if (string.IsNullOrWhiteSpace(o.Host))
        {
            logger.LogInformation("SMTP not configured. Alert email to {To}: {Subject}\n{Body}", to.Address, message.Subject, message.Body);
            return new NotifyResult(true, NotConfiguredNote);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds));
        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)timeout.TotalMilliseconds
        };
        if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);

        using var mail = new MailMessage(new MailAddress(o.From), to)
        {
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await client.SendMailAsync(mail, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return NotifyResult.Transient($"SMTP timeout after {timeout.TotalSeconds:0.#}s");
        }
        return NotifyResult.Ok;
    }
}

/// <summary>
/// Webhook: POST application/json <c>{ monitorId, monitorName, target, status: "down"|"up"|"test"|"certificate_expiring", error,
/// occurredAtUtc, certificateExpiresAtUtc }</c> (certificateExpiresAtUtc is only set for "certificate_expiring").
/// Any 2xx response is success. Redirects are not followed; timeout 10s (configured on the named client).
/// </summary>
public sealed class WebhookNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public AlertChannelType Type => AlertChannelType.Webhook;

    public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
    {
        var payload = new WebhookPayload(message.MonitorId, message.MonitorName, message.Target, message.StatusText,
            message.Error, message.OccurredAtUtc, message.CertificateExpiresAtUtc);
        return WebhookPost.JsonAsync(httpClientFactory, target.Address, payload, "Webhook", ct);
    }
}

/// <summary>Webhook body; serialized with web (camelCase) defaults.</summary>
public sealed record WebhookPayload(int MonitorId, string MonitorName, string Target, string Status, string? Error, DateTime OccurredAtUtc,
    DateTime? CertificateExpiresAtUtc = null);

/// <summary>
/// Shared POST logic for every HTTP-based channel. Always uses the guarded <see cref="MonitoringHttpClients.Webhook"/>
/// client (TargetGuard, no redirects, 10s timeout, request logging off because URLs carry secrets).
/// </summary>
internal static class WebhookPost
{
    public static Task<NotifyResult> JsonAsync<T>(IHttpClientFactory factory, string? url, T payload, string what, CancellationToken ct, string? secret = null)
    {
        if (!TryParseHttpUrl(url, out var uri))
            return Task.FromResult(NotifyResult.Fail($"Invalid {what} URL '{SecretMasker.MaskWebhookUrl(url)}'"));
        return SendAsync(factory, () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) }, what, ct, secret);
    }

    /// <param name="idempotent">
    /// False for requests that must not be repeated when their outcome is unknown (Twilio): only HTTP 429 / 503 and
    /// failures before the request was sent (DNS, connect, TLS) are then retryable; timeouts and other errors are final.
    /// </param>
    public static async Task<NotifyResult> SendAsync(IHttpClientFactory factory, Func<HttpRequestMessage> request, string what, CancellationToken ct,
        string? secret = null, bool idempotent = true)
    {
        var client = factory.CreateClient(MonitoringHttpClients.Webhook);
        try
        {
            using var req = request();
            using var response = await client.SendAsync(req, ct);
            if (response.IsSuccessStatusCode) return NotifyResult.Ok;
            if (idempotent) return NotifyResult.ForHttpStatus(what, response.StatusCode);
            var error = $"{what} returned HTTP {(int)response.StatusCode}";
            return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                ? NotifyResult.Transient(error) : NotifyResult.Fail(error);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var error = SecretMasker.Redact(ProbeErrors.Describe(ex, client.Timeout), secret)!;
            return idempotent || IsBeforeSend(ex) ? NotifyResult.Transient(error) : NotifyResult.Fail(error);
        }
    }

    /// <summary>The request never reached the server: DNS, connect or TLS handshake failed.</summary>
    public static bool IsBeforeSend(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError };

    public static bool TryParseHttpUrl(string? url, out Uri uri) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
