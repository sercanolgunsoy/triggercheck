using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Monitoring.Escalation;

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
    /// Records an alert that was never sent because a newer alert for the same monitor replaced it while it was still
    /// waiting: one failed Notification (0 attempts) per channel it would have gone to.
    /// </summary>
    Task RecordSupersededAsync(AlertMessage message, CancellationToken ct = default);

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
/// SMS / voice deliveries are capped per customer and UTC day (<see cref="AlertingOptions.SmsVoiceDailyLimitPerCustomer"/>):
/// a slot is reserved atomically in <see cref="SmsVoiceUsage"/> before sending and given back only when the request
/// certainly did not reach the provider.
/// Certificate-expiry warnings (<see cref="AlertKind.CertificateExpiring"/>) never go to voice-call channels: they are
/// not urgent enough to phone someone (SMS is fine). A customer whose only channel is a voice call gets them by the
/// contact e-mail fallback.
/// Escalation alerts (FEAT-5) go only to their step's channels (<see cref="AlertMessage.ChannelIds"/>, same fallback when
/// none of them can deliver) and are dropped when the incident was acknowledged or resolved before delivery or before a
/// retry. DOWN and
/// escalation alerts get the signed acknowledge link (<see cref="IncidentAckLinks"/>) when it is configured.
/// Every final per-channel delivery outcome is counted in <see cref="MonitoringMetrics.RecordAlert"/> (not channel
/// tests and not alerts superseded before they were sent).
/// AI-4 (A3): before delivery, a DOWN / UP alert may be folded into its incident group's primary notification by
/// <see cref="IAlertGrouping"/> (deterministic, fails open); it is then recorded per channel with the reason, not sent.
/// </summary>
public sealed class AlertDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<INotifier> notifiers,
    AlertDeliveryThrottle throttle,
    IOptions<AlertingOptions> options,
    IOptionsMonitor<TwilioOptions> twilio,
    TimeProvider time,
    MonitoringMetrics metrics,
    IncidentAckLinks ackLinks,
    ILogger<AlertDispatcher> logger,
    IAlertGrouping? grouping = null) : IAlertDispatcher
{
    public const string NotInPlanError = "SMS and voice call alerts are not included in the plan";
    public const string AbortedError = "Delivery aborted (shutdown)";
    public const string SupersededError = "Superseded by a newer alert for this monitor";
    public const string DailyLimitError = "Daily SMS / voice call limit reached";
    public const string UsageUnavailableError = "SMS / voice limit could not be checked; not sent";
    public const string AcknowledgedError = "Not retried: the incident was acknowledged or resolved";

    public async Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default,
        CancellationToken stopRetrying = default, CancellationToken superseded = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (message.Kind == AlertKind.Escalation && !await IsStillUnacknowledgedAsync(db, message, ct))
        {
            logger.LogInformation("Escalation of incident {IncidentId} (monitor {MonitorId}) not sent: acknowledged or resolved meanwhile",
                message.IncidentId, message.MonitorId);
            return [];
        }
        // AI-4: a held-back DOWN whose incident recovered meanwhile is stale (its UP is, or was, on its way): not sent.
        if (message is { Kind: AlertKind.Down, Deferred: true } && !await IsIncidentOpenAsync(db, message, ct))
        {
            logger.LogInformation("Held-back DOWN of incident {IncidentId} (monitor {MonitorId}) not sent: resolved meanwhile",
                message.IncidentId, message.MonitorId);
            return [];
        }
        // AI-4 (A3): a DOWN / UP covered by its group's primary notification is recorded, not sent. Fails open.
        if (await TryFoldAsync(message, ct) is { } folded)
        {
            await RecordUnsentAsync(db, message, folded, ct);
            return [];
        }
        if (message is { IsDownLike: true, IncidentId: int incidentId, AckUrl: null })
            message = message with { AckUrl = ackLinks.CreateUrl(incidentId, message.CustomerId) };
        var deliveries = await ResolveAsync(db, message, ct);
        Notification[] notifications = [];
        if (deliveries.Count > 0)
        {
            notifications = await Task.WhenAll(deliveries.Select(d => DeliverAsync(d.Target, d.Blocked, message, ct, stopRetrying, superseded)));
            await SaveAsync(db, message, notifications);
        }
        await AfterDeliveredAsync(message, ct);
        return notifications;
    }

    public async Task RecordSupersededAsync(AlertMessage message, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        logger.LogInformation("Alert '{Subject}' for monitor {MonitorId} superseded by a newer alert before it was sent", message.Subject, message.MonitorId);
        await RecordUnsentAsync(db, message, SupersededError, ct);
    }

    /// <summary>One failed Notification (0 attempts) with <paramref name="error"/> per channel the alert would have gone to.</summary>
    private async Task RecordUnsentAsync(AppDbContext db, AlertMessage message, string error, CancellationToken ct)
    {
        var deliveries = await ResolveAsync(db, message, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var notifications = deliveries.Select(d => new Notification
        {
            MonitorId = message.MonitorId,
            AlertChannelId = d.Target.ChannelId,
            SentAtUtc = now,
            Subject = message.Subject,
            Success = false,
            Error = ProbeErrors.Truncate(error),
            Attempts = 0
        }).ToArray();
        await SaveAsync(db, message, notifications);
    }

    /// <summary>AI-4: asks the notification grouping (DOWN / UP only). Any error delivers the alert normally.</summary>
    private async Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct)
    {
        if (grouping is null || message.Kind is not (AlertKind.Down or AlertKind.Up) || message.MonitorId == 0) return null;
        try
        {
            var reason = await grouping.TryFoldAsync(message, ct);
            if (reason is not null)
                logger.LogInformation("Alert '{Subject}' for monitor {MonitorId} grouped: {Reason}", message.Subject, message.MonitorId, reason);
            return reason;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Alert grouping failed for monitor {MonitorId}; sending the alert on its own", message.MonitorId);
            return null;
        }
    }

    /// <summary>AI-4: follow-up work of the grouping once an alert was sent (e.g. a primary's UP undoes its group).</summary>
    private async Task AfterDeliveredAsync(AlertMessage message, CancellationToken ct)
    {
        if (grouping is null || message.Kind is not (AlertKind.Down or AlertKind.Up) || message.MonitorId == 0) return;
        try
        {
            await grouping.AfterDeliveredAsync(message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Alert grouping follow-up failed for monitor {MonitorId}", message.MonitorId);
        }
    }

    private async Task<bool> IsStillUnacknowledgedAsync(AlertMessage message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await IsStillUnacknowledgedAsync(db, message, ct);
    }

    /// <summary>AI-4: the alert's incident (tenant-checked) is still open.</summary>
    private static Task<bool> IsIncidentOpenAsync(AppDbContext db, AlertMessage message, CancellationToken ct) =>
        db.Incidents.AnyAsync(i => i.Id == message.IncidentId && i.Monitor.CustomerId == message.CustomerId && i.ResolvedAtUtc == null, ct);

    /// <summary>FEAT-5: an escalation is only delivered while its incident is still open and unacknowledged.</summary>
    private static Task<bool> IsStillUnacknowledgedAsync(AppDbContext db, AlertMessage message, CancellationToken ct) =>
        db.Incidents.AnyAsync(i => i.Id == message.IncidentId && i.Monitor.CustomerId == message.CustomerId
                                   && i.ResolvedAtUtc == null && i.AcknowledgedAtUtc == null, ct);

    /// <summary>The channels an alert goes to, each with the reason it cannot be delivered (null = deliverable).</summary>
    private async Task<List<(Target Target, string? Blocked)>> ResolveAsync(AppDbContext db, AlertMessage message, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking()
            .Where(c => c.Id == message.CustomerId)
            .Select(c => new { c.ContactEmail, c.AlertContactEmailWhenNoChannel, c.Plan.AllowsSmsVoice })
            .FirstOrDefaultAsync(ct);
        if (customer is null)
        {
            logger.LogWarning("Customer {CustomerId} not found; alert '{Subject}' not delivered", message.CustomerId, message.Subject);
            return [];
        }

        var query = db.AlertChannels.AsNoTracking()
            .Where(c => c.CustomerId == message.CustomerId && c.IsEnabled)
            .Where(c => (message.Kind != AlertKind.CertificateExpiring && message.Kind != AlertKind.GroupUpdate) || c.Type != AlertChannelType.VoiceCall);
        if (message.ChannelIds is { } only)
        {
            var ids = only.ToList();
            query = query.Where(c => ids.Contains(c.Id));
        }
        var channels = await query
            .OrderBy(c => c.Id)
            .Select(c => new Target((int?)c.Id, c.Type, c.Target, c.Secret))
            .ToListAsync(ct);

        var deliveries = channels.Select(t => (Target: t, Blocked: Undeliverable(t.Type, customer.AllowsSmsVoice, twilio.CurrentValue))).ToList();
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
        return deliveries;
    }

    private async Task SaveAsync(AppDbContext db, AlertMessage message, Notification[] notifications)
    {
        if (notifications.Length == 0) return;
        // The monitor may have been deleted while we were sending (Notifications have no FK): do not leave orphans.
        if (message.MonitorId != 0 && !await db.Monitors.AnyAsync(m => m.Id == message.MonitorId, CancellationToken.None))
        {
            logger.LogDebug("Monitor {MonitorId} was deleted during alert delivery; notifications not recorded", message.MonitorId);
            return;
        }
        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync(CancellationToken.None);
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

    /// <summary>
    /// Takes one SMS / voice slot of today's budget atomically (SQLite serializes the conditional UPDATE, so concurrent
    /// deliveries cannot overshoot). Returns the UTC day charged, or null when the limit is reached.
    /// </summary>
    private async Task<DateTime?> TryReserveSmsVoiceAsync(int customerId, CancellationToken ct)
    {
        var limit = options.Value.SmsVoiceDailyLimitPerCustomer;
        var day = time.GetUtcNow().UtcDateTime.Date;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        for (var i = 0; i < 2; i++)
        {
            var taken = await db.SmsVoiceUsage
                .Where(u => u.CustomerId == customerId && u.DayUtc == day && u.Count < limit)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + 1), ct);
            if (taken == 1) return day;
            if (await db.SmsVoiceUsage.AnyAsync(u => u.CustomerId == customerId && u.DayUtc == day, ct)) return null;
            try
            {
                db.SmsVoiceUsage.Add(new SmsVoiceUsage { CustomerId = customerId, DayUtc = day, Count = 0 });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // created concurrently: retry the update
            }
            db.ChangeTracker.Clear();
        }
        return null;
    }

    private async Task ReleaseSmsVoiceAsync(int customerId, DateTime day)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        await db.SmsVoiceUsage
            .Where(u => u.CustomerId == customerId && u.DayUtc == day && u.Count > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count - 1));
    }

    private async Task<Notification> DeliverAsync(Target target, string? blocked, AlertMessage message,
        CancellationToken ct, CancellationToken stopRetrying, CancellationToken superseded)
    {
        NotifyResult result;
        var attempts = 0;
        DateTime? reservedDay = null;
        var maybeSent = false; // some attempt may have reached the provider (success or an unknown / final outcome)
        if (blocked is null && IsSmsOrVoice(target.Type))
        {
            try
            {
                reservedDay = await TryReserveSmsVoiceAsync(message.CustomerId, ct);
                if (reservedDay is null) blocked = DailyLimitError;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Fail closed (cost protection) but keep the other channels and this row.
                logger.LogError(ex, "Could not reserve the SMS / voice budget of customer {CustomerId}", message.CustomerId);
                blocked = UsageUnavailableError;
            }
        }
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
                maybeSent |= result.Success || !result.Retryable;
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
                // FEAT-5: an escalation acknowledged or resolved while waiting is not retried.
                if (message.Kind == AlertKind.Escalation && !await IsStillUnacknowledgedAsync(message, ct))
                {
                    result = NotifyResult.Fail(AcknowledgedError);
                    break;
                }
            }
        }

        // Only failures that certainly did not reach the provider (retryable for a non-idempotent channel means
        // 429 / 503 / not sent at all) give the SMS / voice slot back.
        if (reservedDay is { } day && !maybeSent)
        {
            try
            {
                await ReleaseSmsVoiceAsync(message.CustomerId, day);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not give back an SMS / voice slot of customer {CustomerId}", message.CustomerId);
            }
        }

        Log(target, message, result, attempts);
        metrics.RecordAlert(target.Type, result.Success, attempted: attempts > 0);
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
