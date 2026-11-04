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
/// </summary>
public sealed class EmailNotifier(IOptionsMonitor<SmtpOptions> options, ILogger<EmailNotifier> logger) : INotifier
{
    public const string NotConfiguredNote = "SMTP not configured; email logged only";

    public AlertChannelType Type => AlertChannelType.Email;

    public async Task<NotifyResult> SendAsync(string target, AlertMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!MailAddress.TryCreate(target?.Trim(), out var to))
            return NotifyResult.Fail($"Invalid email address '{target}'");

        if (string.IsNullOrWhiteSpace(o.Host))
        {
            logger.LogInformation("SMTP not configured. Alert email to {To}: {Subject}\n{Body}", to.Address, message.Subject, message.Body);
            return new NotifyResult(true, NotConfiguredNote);
        }

        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = Math.Max(1, o.TimeoutSeconds) * 1000
        };
        if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);

        using var mail = new MailMessage(new MailAddress(o.From), to)
        {
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };
        await client.SendMailAsync(mail, ct);
        return NotifyResult.Ok;
    }
}

/// <summary>
/// Webhook: POST application/json <c>{ monitorId, monitorName, target, status: "down"|"up", error, occurredAtUtc }</c>.
/// Any 2xx response is success. Redirects are not followed; timeout 10s (configured on the named client).
/// </summary>
public sealed class WebhookNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public AlertChannelType Type => AlertChannelType.Webhook;

    public async Task<NotifyResult> SendAsync(string target, AlertMessage message, CancellationToken ct)
    {
        if (!Uri.TryCreate(target?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return NotifyResult.Fail($"Invalid webhook URL '{SecretMasker.MaskWebhookUrl(target)}'");

        var payload = new WebhookPayload(message.MonitorId, message.MonitorName, message.Target, message.StatusText,
            message.Error, message.OccurredAtUtc);
        var client = httpClientFactory.CreateClient(MonitoringHttpClients.Webhook);
        try
        {
            using var response = await client.PostAsJsonAsync(uri, payload, ct);
            return response.IsSuccessStatusCode
                ? NotifyResult.Ok
                : NotifyResult.Fail($"Webhook returned HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return NotifyResult.Fail(ProbeErrors.Describe(ex, client.Timeout));
        }
    }
}

/// <summary>Webhook body; serialized with web (camelCase) defaults.</summary>
public sealed record WebhookPayload(int MonitorId, string MonitorName, string Target, string Status, string? Error, DateTime OccurredAtUtc);
