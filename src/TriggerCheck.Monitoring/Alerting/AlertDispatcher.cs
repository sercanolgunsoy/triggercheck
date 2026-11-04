using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Monitoring.Alerting;

public interface IAlertDispatcher
{
    /// <summary>
    /// Sends the alert to every enabled channel of the customer (with retries) and records one Notification per
    /// channel. Never throws for delivery errors. <paramref name="ct"/> aborts in-flight sends (recorded as failed);
    /// <paramref name="stopRetrying"/> only ends pending retry waits (the last failure is recorded).
    /// </summary>
    Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default, CancellationToken stopRetrying = default);

    /// <summary>
    /// "Send test notification": one attempt (with timeout) to the customer's channel, enabled or not. Nothing is
    /// recorded. Returns null when the channel does not exist or belongs to another customer.
    /// </summary>
    Task<NotifyResult?> SendTestAsync(int customerId, int channelId, CancellationToken ct = default);
}

/// <summary>
/// Fans an alert out to all enabled <see cref="AlertChannel"/>s of the monitor's customer (in parallel) and stores one
/// <see cref="Notification"/> row per channel with the final outcome and the number of attempts. Each attempt is
/// bounded by <see cref="AlertingOptions.SendTimeoutSeconds"/>; transient failures are retried after
/// <see cref="AlertingOptions.RetryDelays"/>.
/// If the customer has no enabled channel, the alert is e-mailed to <c>Customer.ContactEmail</c>
/// (Notification.AlertChannelId = null) unless the customer turned that off
/// (<see cref="Customer.AlertContactEmailWhenNoChannel"/>).
/// SMS / voice channels are only delivered when the customer's plan allows them (<see cref="Plan.AllowsSmsVoice"/>).
/// </summary>
public sealed class AlertDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<INotifier> notifiers,
    IOptions<AlertingOptions> options,
    TimeProvider time,
    ILogger<AlertDispatcher> logger) : IAlertDispatcher
{
    public const string NotInPlanError = "SMS and voice call alerts are not included in the plan";
    public const string AbortedError = "Delivery aborted (shutdown)";

    public async Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default, CancellationToken stopRetrying = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var customer = await db.Customers.AsNoTracking()
            .Where(c => c.Id == message.CustomerId)
            .Select(c => new { c.ContactEmail, c.AlertContactEmailWhenNoChannel, c.Plan.AllowsSmsVoice })
            .FirstOrDefaultAsync(ct);
        if (customer is null)
        {
            logger.LogWarning("Customer {CustomerId} not found; alert '{Subject}' not delivered", message.CustomerId, message.Subject);
            return [];
        }

        var targets = await db.AlertChannels.AsNoTracking()
            .Where(c => c.CustomerId == message.CustomerId && c.IsEnabled)
            .OrderBy(c => c.Id)
            .Select(c => new Target((int?)c.Id, c.Type, c.Target, c.Secret))
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            if (!customer.AlertContactEmailWhenNoChannel)
            {
                logger.LogInformation("Customer {CustomerId} has no enabled alert channel and the contact e-mail fallback is off; alert '{Subject}' not sent",
                    message.CustomerId, message.Subject);
                return [];
            }
            if (string.IsNullOrWhiteSpace(customer.ContactEmail))
            {
                logger.LogWarning("No alert channel and no contact email for customer {CustomerId}; alert '{Subject}' not delivered",
                    message.CustomerId, message.Subject);
                return [];
            }
            targets.Add(new Target(null, AlertChannelType.Email, customer.ContactEmail, null));
        }

        var notifications = await Task.WhenAll(targets.Select(t => DeliverAsync(t, message, customer.AllowsSmsVoice, ct, stopRetrying)));

        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync(CancellationToken.None);
        return notifications;
    }

    public async Task<NotifyResult?> SendTestAsync(int customerId, int channelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.AlertChannels.AsNoTracking()
            .Where(c => c.Id == channelId && c.CustomerId == customerId)
            .Select(c => new { Target = new Target((int?)c.Id, c.Type, c.Target, c.Secret), c.Customer.Plan.AllowsSmsVoice })
            .FirstOrDefaultAsync(ct);
        if (channel is null) return null;

        var message = AlertMessage.TestFor(customerId, time.GetUtcNow().UtcDateTime);
        var result = IsSmsOrVoice(channel.Target.Type) && !channel.AllowsSmsVoice
            ? NotifyResult.Fail(NotInPlanError)
            : await AttemptAsync(channel.Target, message, ct);
        Log(channel.Target, message, result, 1);
        return result;
    }

    public static bool IsSmsOrVoice(AlertChannelType type) => type is AlertChannelType.Sms or AlertChannelType.VoiceCall;

    private async Task<Notification> DeliverAsync(Target target, AlertMessage message, bool allowsSmsVoice, CancellationToken ct, CancellationToken stopRetrying)
    {
        NotifyResult result;
        var attempts = 0;
        if (IsSmsOrVoice(target.Type) && !allowsSmsVoice)
        {
            result = NotifyResult.Fail(NotInPlanError);
        }
        else
        {
            while (true)
            {
                attempts++;
                result = await AttemptAsync(target, message, ct);
                if (result.Success || !result.Retryable || attempts >= AlertingOptions.MaxAttempts
                    || ct.IsCancellationRequested || stopRetrying.IsCancellationRequested)
                    break;

                logger.LogInformation("Alert '{Subject}' via {Type} to {Target} failed (attempt {Attempt}): {Error}; retrying",
                    message.Subject, target.Type, Shown(target), attempts, result.Error);
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, stopRetrying);
                    await Task.Delay(AlertingOptions.RetryDelays[attempts - 1], time, wait.Token);
                }
                catch (OperationCanceledException)
                {
                    break; // shutting down: record the last failure
                }
            }
        }

        Log(target, message, result, attempts);
        return new Notification
        {
            MonitorId = message.MonitorId,
            AlertChannelId = target.ChannelId,
            SentAtUtc = time.GetUtcNow().UtcDateTime,
            Subject = message.Subject,
            Success = result.Success,
            Error = ProbeErrors.Truncate(result.Error),
            Attempts = attempts
        };
    }

    /// <summary>One delivery attempt, bounded by <see cref="AlertingOptions.SendTimeoutSeconds"/> even if the notifier ignores its token.</summary>
    private async Task<NotifyResult> AttemptAsync(Target target, AlertMessage message, CancellationToken ct)
    {
        var notifier = notifiers.FirstOrDefault(n => n.Type == target.Type);
        if (notifier is null) return NotifyResult.Fail($"No notifier registered for channel type {target.Type}");

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SendTimeoutSeconds));
        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await notifier.SendAsync(new NotifyTarget(target.Address, target.Secret), message, linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NotifyResult.Fail(AbortedError);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return NotifyResult.Transient($"Timeout after {timeout.TotalSeconds:0.#}s");
        }
        catch (Exception ex)
        {
            // Exception text can carry the target (e.g. a URL with a token): describe it and strip the secret.
            return NotifyResult.Transient(SecretMasker.Redact(ProbeErrors.Describe(ex, timeout), target.Secret)!);
        }
    }

    private void Log(Target target, AlertMessage message, NotifyResult result, int attempts)
    {
        if (result.Success)
            logger.LogInformation("Alert '{Subject}' sent via {Type} to {Target}", message.Subject, target.Type, Shown(target));
        else
            logger.LogWarning("Alert '{Subject}' via {Type} to {Target} failed after {Attempts} attempt(s): {Error}",
                message.Subject, target.Type, Shown(target), attempts, result.Error);
    }

    /// <summary>Webhook-style URLs carry their secret in the path: never log them in full. Bot tokens are never logged.</summary>
    private static string Shown(Target t) => t.Type is AlertChannelType.Webhook or AlertChannelType.Slack or AlertChannelType.Teams
        ? SecretMasker.MaskWebhookUrl(t.Address)
        : t.Address;

    private sealed record Target(int? ChannelId, AlertChannelType Type, string Address, string? Secret)
    {
        public override string ToString() => $"{Type} {Shown(this)}";
    }
}
