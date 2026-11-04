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
    /// Sends the alert to every enabled channel of the customer (each channel retried on its own) and records one
    /// Notification per channel. Never throws for delivery errors.
    /// <paramref name="ct"/> aborts in-flight sends (recorded as failed); <paramref name="stopRetrying"/> ends pending
    /// retry waits (the last failure is recorded); <paramref name="superseded"/> does the same but records the
    /// delivery as superseded by a newer alert for the same monitor.
    /// </summary>
    Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default,
        CancellationToken stopRetrying = default, CancellationToken superseded = default);

    /// <summary>
    /// "Send test notification": one attempt (with timeout) to the customer's channel, enabled or not. Nothing is
    /// recorded. Returns null when the channel does not exist or belongs to another customer.
    /// </summary>
    Task<NotifyResult?> SendTestAsync(int customerId, int channelId, CancellationToken ct = default);
}

/// <summary>
/// Fans an alert out to all enabled <see cref="AlertChannel"/>s of the monitor's customer (in parallel) and stores one
/// <see cref="Notification"/> row per channel with the final outcome and the number of attempts. Each attempt is
/// bounded by <see cref="AlertingOptions.SendTimeoutSeconds"/> and throttled by <see cref="AlertDeliveryThrottle"/>;
/// transient failures are retried after <see cref="AlertingOptions.RetryDelays"/> (no slot is held while waiting).
/// Channels that cannot deliver (SMS / voice when the plan does not include them or Twilio is not configured) are
/// recorded as failed. If no deliverable channel is enabled, the alert is e-mailed to <c>Customer.ContactEmail</c>
/// (Notification.AlertChannelId = null) unless the customer turned that off
/// (<see cref="Customer.AlertContactEmailWhenNoChannel"/>).
/// SMS / voice deliveries are capped per customer and UTC day (<see cref="AlertingOptions.SmsVoiceDailyLimitPerCustomer"/>).
/// </summary>
public sealed class AlertDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<INotifier> notifiers,
    AlertDeliveryThrottle throttle,
    IOptions<AlertingOptions> options,
    IOptionsMonitor<TwilioOptions> twilio,
    TimeProvider time,
    ILogger<AlertDispatcher> logger) : IAlertDispatcher
{
    public const string NotInPlanError = "SMS and voice call alerts are not included in the plan";
    public const string AbortedError = "Delivery aborted (shutdown)";
    public const string SupersededError = "Superseded by a newer alert for this monitor";
    public const string DailyLimitError = "Daily SMS / voice call limit reached";

    public async Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default,
        CancellationToken stopRetrying = default, CancellationToken superseded = default)
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

        var channels = await db.AlertChannels.AsNoTracking()
            .Where(c => c.CustomerId == message.CustomerId && c.IsEnabled)
            .OrderBy(c => c.Id)
            .Select(c => new Target((int?)c.Id, c.Type, c.Target, c.Secret))
            .ToListAsync(ct);

        var smsVoiceAllowed = customer.AllowsSmsVoice;
        var deliveries = channels.Select(t => (Target: t, Blocked: Undeliverable(t.Type, smsVoiceAllowed, twilio.CurrentValue))).ToList();

        if (deliveries.All(d => d.Blocked is not null))
        {
            if (!customer.AlertContactEmailWhenNoChannel)
                logger.LogInformation("Customer {CustomerId} has no deliverable alert channel and the contact e-mail fallback is off; alert '{Subject}' not sent",
                    message.CustomerId, message.Subject);
            else if (string.IsNullOrWhiteSpace(customer.ContactEmail))
                logger.LogWarning("No deliverable alert channel and no contact email for customer {CustomerId}; alert '{Subject}' not delivered",
                    message.CustomerId, message.Subject);
            else
                deliveries.Add((new Target(null, AlertChannelType.Email, customer.ContactEmail, null), null));
        }
        if (deliveries.Count == 0) return [];

        // Daily SMS / voice budget (cost protection). Concurrent alerts of one customer may overshoot it slightly.
        var smsVoice = deliveries.Count(d => d.Blocked is null && IsSmsOrVoice(d.Target.Type));
        if (smsVoice > 0)
        {
            var left = options.Value.SmsVoiceDailyLimitPerCustomer - await CountSmsVoiceTodayAsync(db, message.CustomerId, ct);
            for (var i = 0; i < deliveries.Count; i++)
                if (deliveries[i].Blocked is null && IsSmsOrVoice(deliveries[i].Target.Type) && left-- <= 0)
                    deliveries[i] = (deliveries[i].Target, DailyLimitError);
        }

        var notifications = await Task.WhenAll(deliveries.Select(d => DeliverAsync(d.Target, d.Blocked, message, ct, stopRetrying, superseded)));

        // The monitor may have been deleted while we were sending (Notifications have no FK): do not leave orphans.
        if (message.MonitorId != 0 && !await db.Monitors.AnyAsync(m => m.Id == message.MonitorId, CancellationToken.None))
        {
            logger.LogDebug("Monitor {MonitorId} was deleted during alert delivery; notifications not recorded", message.MonitorId);
            return notifications;
        }
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
        var blocked = Undeliverable(channel.Target.Type, channel.AllowsSmsVoice, twilio.CurrentValue);
        var result = blocked is not null ? NotifyResult.Fail(blocked) : await AttemptAsync(channel.Target, message, ct, throttled: false);
        Log(channel.Target, message, result, 1);
        return result;
    }

    public static bool IsSmsOrVoice(AlertChannelType type) => type is AlertChannelType.Sms or AlertChannelType.VoiceCall;

    /// <summary>Why a channel of this type can never deliver for this customer, or null when it can.</summary>
    public static string? Undeliverable(AlertChannelType type, bool planAllowsSmsVoice, TwilioOptions twilio) =>
        !IsSmsOrVoice(type) ? null
        : !planAllowsSmsVoice ? NotInPlanError
        : !twilio.IsConfigured ? TwilioNotifier.NotConfiguredError
        : null;

    /// <summary>SMS / voice deliveries actually attempted today (UTC) for the customer.</summary>
    private async Task<int> CountSmsVoiceTodayAsync(AppDbContext db, int customerId, CancellationToken ct)
    {
        var dayStart = time.GetUtcNow().UtcDateTime.Date;
        return await db.Notifications
            .Where(n => n.SentAtUtc >= dayStart && n.Attempts > 0)
            .Join(db.AlertChannels.Where(c => c.CustomerId == customerId && (c.Type == AlertChannelType.Sms || c.Type == AlertChannelType.VoiceCall)),
                n => n.AlertChannelId, c => (int?)c.Id, (n, c) => n.Id)
            .CountAsync(ct);
    }

    private async Task<Notification> DeliverAsync(Target target, string? blocked, AlertMessage message,
        CancellationToken ct, CancellationToken stopRetrying, CancellationToken superseded)
    {
        NotifyResult result;
        var attempts = 0;
        if (blocked is not null)
        {
            result = NotifyResult.Fail(blocked);
        }
        else
        {
            while (true)
            {
                attempts++;
                result = await AttemptAsync(target, message, ct, throttled: true);
                if (result.Success || !result.Retryable || attempts >= AlertingOptions.MaxAttempts
                    || ct.IsCancellationRequested || stopRetrying.IsCancellationRequested)
                    break;
                if (superseded.IsCancellationRequested)
                {
                    result = Superseded(result);
                    break;
                }

                logger.LogInformation("Alert '{Subject}' via {Type} to {Target} failed (attempt {Attempt}): {Error}; retrying",
                    message.Subject, target.Type, Shown(target), attempts, result.Error);
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, stopRetrying, superseded);
                    await Task.Delay(AlertingOptions.RetryDelays[attempts - 1], time, wait.Token);
                }
                catch (OperationCanceledException)
                {
                    if (superseded.IsCancellationRequested && !ct.IsCancellationRequested && !stopRetrying.IsCancellationRequested)
                        result = Superseded(result);
                    break; // shutting down or superseded: record the last failure
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

    private static NotifyResult Superseded(NotifyResult last) => NotifyResult.Fail($"{SupersededError} (last error: {last.Error})");

    /// <summary>One delivery attempt, bounded by <see cref="AlertingOptions.SendTimeoutSeconds"/> even if the notifier ignores its token.</summary>
    private async Task<NotifyResult> AttemptAsync(Target target, AlertMessage message, CancellationToken ct, bool throttled)
    {
        var notifier = notifiers.FirstOrDefault(n => n.Type == target.Type);
        if (notifier is null) return NotifyResult.Fail($"No notifier registered for channel type {target.Type}");

        IDisposable? slot = null;
        try
        {
            if (throttled) slot = await throttle.AcquireAsync(message.CustomerId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NotifyResult.Fail(AbortedError);
        }

        using (slot)
        {
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
                // The request may have been processed: only idempotent channels are retried.
                var error = $"Timeout after {timeout.TotalSeconds:0.#}s";
                return notifier.IsIdempotent ? NotifyResult.Transient(error) : NotifyResult.Fail(error);
            }
            catch (Exception ex)
            {
                // Exception text can carry the target (e.g. a URL with a token): describe it and strip the secret.
                var error = SecretMasker.Redact(ProbeErrors.Describe(ex, timeout), target.Secret)!;
                return notifier.IsIdempotent || WebhookPost.IsBeforeSend(ex) ? NotifyResult.Transient(error) : NotifyResult.Fail(error);
            }
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

    /// <summary>
    /// Webhook-style URLs carry their secret in the path: never log them in full. Phone numbers are masked; bot tokens
    /// are never logged.
    /// </summary>
    private static string Shown(Target t) => t.Type switch
    {
        AlertChannelType.Webhook or AlertChannelType.Slack or AlertChannelType.Teams => SecretMasker.MaskWebhookUrl(t.Address),
        AlertChannelType.Sms or AlertChannelType.VoiceCall => PhoneNumber.Mask(t.Address),
        _ => t.Address
    };

    private sealed record Target(int? ChannelId, AlertChannelType Type, string Address, string? Secret)
    {
        public override string ToString() => $"{Type} {Shown(this)}";
    }
}
