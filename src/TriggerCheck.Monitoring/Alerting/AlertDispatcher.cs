using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Monitoring.Alerting;

public interface IAlertDispatcher
{
    /// <summary>Sends the alert to every enabled channel of the customer and records each attempt. Never throws for delivery errors.</summary>
    Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default);
}

/// <summary>
/// Fans an alert out to all enabled <see cref="AlertChannel"/>s of the monitor's customer (in parallel) and stores one
/// <see cref="Notification"/> row per attempt. If the customer has no enabled channel, the alert is emailed to
/// <c>Customer.ContactEmail</c> (Notification.AlertChannelId = null) so that outages are never silently dropped.
/// </summary>
public sealed class AlertDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<INotifier> notifiers,
    TimeProvider time,
    ILogger<AlertDispatcher> logger) : IAlertDispatcher
{
    public async Task<IReadOnlyList<Notification>> DispatchAsync(AlertMessage message, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var targets = await db.AlertChannels.AsNoTracking()
            .Where(c => c.CustomerId == message.CustomerId && c.IsEnabled)
            .OrderBy(c => c.Id)
            .Select(c => new Target((int?)c.Id, c.Type, c.Target))
            .ToListAsync(ct);

        if (targets.Count == 0)
        {
            var contact = await db.Customers.AsNoTracking()
                .Where(c => c.Id == message.CustomerId).Select(c => c.ContactEmail).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(contact))
            {
                logger.LogWarning("No alert channel and no contact email for customer {CustomerId}; alert '{Subject}' not delivered",
                    message.CustomerId, message.Subject);
                return [];
            }
            targets.Add(new Target(null, AlertChannelType.Email, contact));
        }

        var notifications = await Task.WhenAll(targets.Select(t => SendOneAsync(t, message, ct)));

        db.Notifications.AddRange(notifications);
        await db.SaveChangesAsync(CancellationToken.None);
        return notifications;
    }

    private async Task<Notification> SendOneAsync(Target target, AlertMessage message, CancellationToken ct)
    {
        NotifyResult result;
        var notifier = notifiers.FirstOrDefault(n => n.Type == target.Type);
        if (notifier is null)
        {
            result = NotifyResult.Fail($"No notifier registered for channel type {target.Type}");
        }
        else
        {
            try
            {
                result = await notifier.SendAsync(target.Address, message, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                result = NotifyResult.Fail(ProbeErrors.Describe(ex, TimeSpan.Zero));
            }
        }

        if (result.Success)
            logger.LogInformation("Alert '{Subject}' sent via {Type} to {Target}", message.Subject, target.Type, target.Address);
        else
            logger.LogWarning("Alert '{Subject}' via {Type} to {Target} failed: {Error}", message.Subject, target.Type, target.Address, result.Error);

        return new Notification
        {
            MonitorId = message.MonitorId,
            AlertChannelId = target.ChannelId,
            SentAtUtc = time.GetUtcNow().UtcDateTime,
            Subject = message.Subject,
            Success = result.Success,
            Error = ProbeErrors.Truncate(result.Error)
        };
    }

    private sealed record Target(int? ChannelId, AlertChannelType Type, string Address);
}
