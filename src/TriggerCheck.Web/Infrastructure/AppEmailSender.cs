using System.Net;
using System.Net.Mail;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Alerting;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Transactional e-mail of the web app (account confirmation, password reset, billing notices). Plain text.
/// The registered implementation is <see cref="QueuedAppEmailSender"/>: it only queues, so a request never waits for
/// SMTP and its response time does not reveal whether an e-mail was sent (no account enumeration by timing).
/// </summary>
public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Actually delivers one e-mail (SMTP). Used only by <see cref="AppEmailSenderService"/>.</summary>
public interface IAppEmailTransport
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

public sealed record AppEmail(string To, string Subject, string Body);

/// <summary>Bounded in-memory queue between the request path and <see cref="AppEmailSenderService"/>.</summary>
public sealed class AppEmailQueue
{
    public const int Capacity = 1000;
    private readonly Channel<AppEmail> _channel = Channel.CreateBounded<AppEmail>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private int _pending;

    public ChannelReader<AppEmail> Reader => _channel.Reader;
    /// <summary>Queued plus in-delivery e-mails.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>False when the queue is full or shut down (the caller logs; it never waits).</summary>
    public bool TryEnqueue(AppEmail email)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(email)) return true;
        Interlocked.Decrement(ref _pending);
        return false;
    }

    public void Completed() => Interlocked.Decrement(ref _pending);
    public void Complete() => _channel.Writer.TryComplete();
}

public sealed class QueuedAppEmailSender(AppEmailQueue queue, ILogger<QueuedAppEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!queue.TryEnqueue(new AppEmail(to, subject, body)))
            logger.LogError("E-mail queue full or stopped: \"{Subject}\" to {To} was dropped.", subject, to);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Delivers queued e-mails one at a time. Shutdown: the queue stops accepting, queued e-mails get up to 10 seconds,
/// then the rest is dropped (logged). Safe to stop twice: the abort source is never disposed.
/// </summary>
public sealed class AppEmailSenderService(AppEmailQueue queue, IAppEmailTransport transport, ILogger<AppEmailSenderService> logger) : BackgroundService
{
    // Never disposed: StopAsync may run again after Dispose (StopApplication, then disposing the host).
    private readonly CancellationTokenSource _abort = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not stoppingToken: after StopAsync the e-mails already queued are still delivered (bounded by _abort).
            await foreach (var email in queue.Reader.ReadAllAsync(_abort.Token))
            {
                try { await transport.SendAsync(email.To, email.Subject, email.Body, _abort.Token); }
                catch (Exception ex) when (!_abort.IsCancellationRequested)
                {
                    logger.LogError(ex, "Sending e-mail \"{Subject}\" to {To} failed.", email.Subject, email.To);
                }
                finally { queue.Completed(); }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            if (queue.Pending > 0) logger.LogWarning("Shutdown: {Count} queued e-mail(s) were not sent.", queue.Pending);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        _abort.CancelAfter(TimeSpan.FromSeconds(10));
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// SMTP delivery with the same <c>Smtp</c> settings as the e-mail alerts. When SMTP is not configured the message is
/// only logged: in Development with its body (so the confirmation link can be copied, dev only), elsewhere without it
/// (bodies carry account tokens) and as a warning. Send failures are logged, never thrown.
/// </summary>
public sealed class SmtpAppEmailTransport(IOptionsMonitor<SmtpOptions> options, IHostEnvironment env, ILogger<SmtpAppEmailTransport> logger) : IAppEmailTransport
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
