using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Alerting;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>Transactional e-mail of the web app (account confirmation, billing notices). Plain text.</summary>
public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>
/// Sends through the same <c>Smtp</c> settings as the e-mail alerts. When SMTP is not configured the message is only
/// logged: in Development with its body (so the confirmation link can be copied, dev only), elsewhere without it
/// (bodies carry account tokens) and as a warning. Send failures are logged, never thrown to the caller.
/// </summary>
public sealed class SmtpAppEmailSender(IOptionsMonitor<SmtpOptions> options, IHostEnvironment env, ILogger<SmtpAppEmailSender> logger) : IAppEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            if (env.IsDevelopment())
                logger.LogInformation("DEV ONLY - SMTP not configured, e-mail not sent. To: {To}\nSubject: {Subject}\n{Body}", to, subject, body);
            else
                logger.LogWarning("SMTP not configured: e-mail \"{Subject}\" to {To} was not sent.", subject, to);
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds));
        try
        {
            using var client = new SmtpClient(o.Host, o.Port)
            {
                EnableSsl = o.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = (int)timeout.TotalMilliseconds
            };
            if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);
            using var mail = new MailMessage(new MailAddress(o.From), new MailAddress(to)) { Subject = subject, Body = body, IsBodyHtml = false };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.SendMailAsync(mail, cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Sending e-mail \"{Subject}\" to {To} failed.", subject, to);
        }
    }
}
