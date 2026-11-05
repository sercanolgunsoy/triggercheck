using System.Net;
using System.Net.Mail;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Transactional e-mail of the web app (account confirmation, password reset, billing and operator notices). Plain text.
/// The registered implementation is <see cref="OutboxAppEmailSender"/>: it only inserts an <see cref="EmailOutbox"/>
/// row, so a request never waits for SMTP (its response time does not reveal whether an e-mail was sent) and no
/// e-mail is lost on a restart.
/// </summary>
public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Actually delivers one e-mail (SMTP). Throws on failure (the sender retries). Used only by <see cref="AppEmailSenderService"/>.</summary>
public interface IAppEmailTransport
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Wakes the sender as soon as a row was added (otherwise it polls every 30 seconds).</summary>
public sealed class AppEmailSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Notify() => _channel.Writer.TryWrite(true);
    public ChannelReader<bool> Reader => _channel.Reader;
}

/// <summary>
/// Inserts the e-mail into the outbox through its own short-lived context (never flushes the caller's pending changes,
/// never joins the caller's transaction).
/// </summary>
public sealed class OutboxAppEmailSender(IServiceScopeFactory scopes, AppEmailSignal signal, TimeProvider time) : IAppEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.EmailOutbox.Add(new EmailOutbox
        {
            To = to.Length > 256 ? to[..256] : to, Subject = subject.Length > 300 ? subject[..300] : subject, Body = body,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
        signal.Notify();
    }
}

/// <summary>
/// Delivers outbox rows: claims a batch (conditional update, safe with several instances), sends each, retries
/// failures with back-off (1, 5, 15, 60 minutes) and gives up after <see cref="MaxAttempts"/>. Sent rows are purged
/// after <see cref="KeepSentDays"/> days. Logs carry the recipient and subject only, never the body (it may contain
/// account tokens). Stopping is safe at any time and twice: undelivered rows simply stay in the outbox.
/// </summary>
public sealed class AppEmailSenderService(IServiceScopeFactory scopes, IAppEmailTransport transport, AppEmailSignal signal, TimeProvider time,
    ILogger<AppEmailSenderService> logger) : BackgroundService
{
    public const int MaxAttempts = 5;
    public const int KeepSentDays = 7;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];
    private DateTime _lastPurge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await SendBatchAsync(stoppingToken) > 0) { }
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "E-mail outbox processing failed.");
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try { await signal.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Sends the due rows of one batch; returns how many were claimed.</summary>
    public async Task<int> SendBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var ids = await db.EmailOutbox.AsNoTracking()
            .Where(m => m.SentAtUtc == null && m.FailedAtUtc == null && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)
                        && (m.LockedUntilUtc == null || m.LockedUntilUtc <= now))
            .OrderBy(m => m.Id).Select(m => m.Id).Take(20).ToListAsync(ct);
        var claimed = 0;
        foreach (var id in ids)
        {
            // Times per row: a slow send must not shorten the next row's lock or back-off.
            now = time.GetUtcNow().UtcDateTime;
            var lockUntil = now.AddMinutes(2);
            // Back-off re-checked too: another instance may have attempted the row since it was selected (OPS-3).
            if (await db.EmailOutbox.Where(m => m.Id == id && m.SentAtUtc == null && m.FailedAtUtc == null
                                                && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)
                                                && (m.LockedUntilUtc == null || m.LockedUntilUtc <= now))
                    .ExecuteUpdateAsync(u => u.SetProperty(m => m.LockedUntilUtc, lockUntil), ct) == 0) continue;
            claimed++;
            var mail = await db.EmailOutbox.FirstAsync(m => m.Id == id, ct);
            mail.Attempts++;
            try
            {
                await transport.SendAsync(mail.To, mail.Subject, mail.Body ?? "", ct);
                mail.SentAtUtc = time.GetUtcNow().UtcDateTime;
                mail.LastError = null;
                mail.Body = null; // tokens are not kept once delivered
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                now = time.GetUtcNow().UtcDateTime;
                mail.LastError = ex.GetType().Name + ": " + (ex.Message.Length > 400 ? ex.Message[..400] : ex.Message);
                if (mail.Attempts >= MaxAttempts)
                {
                    mail.FailedAtUtc = now;
                    mail.Body = null;
                    logger.LogError(ex, "E-mail \"{Subject}\" to {To} failed {Attempts} times; giving up.", mail.Subject, mail.To, mail.Attempts);
                }
                else
                {
                    mail.NextAttemptAtUtc = now + Backoff[Math.Min(mail.Attempts - 1, Backoff.Length - 1)];
                    logger.LogWarning(ex, "E-mail \"{Subject}\" to {To} failed (attempt {Attempts}); retrying later.", mail.Subject, mail.To, mail.Attempts);
                }
            }
            mail.LockedUntilUtc = null;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return claimed;
    }

    /// <summary>Deletes sent and given-up rows older than <see cref="KeepSentDays"/> (at most hourly).</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastPurge < TimeSpan.FromHours(1)) return;
        _lastPurge = now;
        using var scope = scopes.CreateScope();
        var cutoff = now.AddDays(-KeepSentDays);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.EmailOutbox
            .Where(m => (m.SentAtUtc != null && m.SentAtUtc < cutoff) || (m.FailedAtUtc != null && m.FailedAtUtc < cutoff))
            .ExecuteDeleteAsync(ct);
        // Rows still unsent after a day (e.g. SMTP down for long): give up and drop the body (its links have expired).
        var stale = now.AddDays(-1);
        await db.EmailOutbox.Where(m => m.SentAtUtc == null && m.FailedAtUtc == null && m.CreatedAtUtc < stale)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.FailedAtUtc, now).SetProperty(m => m.Body, (string?)null)
                .SetProperty(m => m.LastError, "Not sent within one day"), ct);
    }
}

/// <summary>
/// SMTP delivery with the same <c>Smtp</c> settings as the e-mail alerts. When SMTP is not configured the message is
/// only logged (counted as sent): in Development with its body (so the confirmation link can be copied, dev only),
/// elsewhere without it (bodies carry account tokens) and as a warning. Send failures throw (the sender retries).
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
        using var client = new SmtpClient(o.Host, o.Port)
        {
            EnableSsl = o.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = (int)timeout.TotalMilliseconds
        };
        if (!string.IsNullOrEmpty(o.User)) client.Credentials = new NetworkCredential(o.User, o.Password);
        using var mail = new MailMessage(new MailAddress(o.From), new MailAddress(to)) { Subject = subject, Body = body, IsBodyHtml = false };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await client.SendMailAsync(mail, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"SMTP timeout after {timeout.TotalSeconds:0}s"); }
    }
}
