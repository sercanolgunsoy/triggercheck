using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Escalation;

/// <summary>FEAT-5: escalations in the per-monitor alert lanes of AlertSenderService.</summary>
public class EscalationLaneTests
{
    /// <summary>Records every attempt; the behaviour decides per attempt (kind, level, attempt number of that message).</summary>
    private sealed class LaneNotifier(AlertChannelType type, Func<AlertMessage, int, NotifyResult> behavior) : INotifier
    {
        private readonly ConcurrentDictionary<AlertMessage, int> _attempts = new(ReferenceEqualityComparer.Instance);
        public ConcurrentQueue<AlertMessage> Calls { get; } = new();
        public AlertChannelType Type => type;

        public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
        {
            Calls.Enqueue(message);
            return Task.FromResult(behavior(message, _attempts.AddOrUpdate(message, 1, (_, n) => n + 1)));
        }
    }

    private static readonly NotifyResult Transient = NotifyResult.Transient("Webhook returned HTTP 503");

    private static async Task<(int CustomerId, Domain.Entities.Monitor Monitor, int IncidentId, int WebhookId, int SlackId)> ArrangeAsync(MonitoringTestHost host)
    {
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.Status = MonitorStatus.Down; m.LastError = "Connection refused"; });
        return await host.WithDbAsync(async db =>
        {
            var webhook = new AlertChannel { CustomerId = customer.Id, Name = "hook", Type = AlertChannelType.Webhook, Target = "https://hooks.example.test/a" };
            var slack = new AlertChannel { CustomerId = customer.Id, Name = "slack", Type = AlertChannelType.Slack, Target = "https://hooks.slack.test/b" };
            var incident = new Incident { MonitorId = monitor.Id, StartedAtUtc = host.Now.AddMinutes(-10) };
            db.AddRange(webhook, slack, incident);
            await db.SaveChangesAsync();
            return (customer.Id, monitor, incident.Id, webhook.Id, slack.Id);
        });
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition not reached");
    }

    private static Task<List<Notification>> NotificationsAsync(MonitoringTestHost host) =>
        host.WithDbAsync(db => db.Notifications.AsNoTracking().OrderBy(n => n.Id).ToListAsync());

    [Fact]
    public async Task Up_supersedes_a_retrying_escalation_of_the_same_monitor()
    {
        var hook = new LaneNotifier(AlertChannelType.Webhook, (m, _) => m.Kind == AlertKind.Escalation ? Transient : NotifyResult.Ok);
        var slack = new LaneNotifier(AlertChannelType.Slack, (_, _) => NotifyResult.Ok);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(hook, slack));
        var (customerId, monitor, incidentId, webhookId, _) = await ArrangeAsync(host);

        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, host.Now.AddMinutes(-10), 1, [webhookId], host.Now)));
        await WaitForAsync(() => hook.Calls.Count == 1); // first attempt failed, now waiting to retry
        Assert.True(host.Alerts.TryEnqueue(AlertMessage.For(monitor, AlertKind.Up, host.Now, TimeSpan.FromMinutes(10))));
        await host.AdvanceUntilAlertsIdleAsync();

        Assert.Single(hook.Calls, m => m.Kind == AlertKind.Escalation); // never retried
        Assert.Single(hook.Calls, m => m.Kind == AlertKind.Up);
        var notifications = await NotificationsAsync(host);
        var escalation = Assert.Single(notifications, n => n.Subject.Contains("ESCALATION"));
        Assert.StartsWith(AlertDispatcher.SupersededError, escalation.Error);
        Assert.All(notifications.Where(n => n.Subject.Contains("UP")), n => Assert.True(n.Success));
        Assert.Equal(customerId, monitor.CustomerId);
    }

    [Fact]
    public async Task Escalation_never_supersedes_the_original_down_delivery()
    {
        var hook = new LaneNotifier(AlertChannelType.Webhook, (m, attempt) => m.Kind == AlertKind.Down && attempt == 1 ? Transient : NotifyResult.Ok);
        var slack = new LaneNotifier(AlertChannelType.Slack, (_, _) => NotifyResult.Ok);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(hook, slack));
        var (_, monitor, incidentId, webhookId, _) = await ArrangeAsync(host);

        Assert.True(host.Alerts.TryEnqueue(AlertMessage.For(monitor, AlertKind.Down, host.Now) with { IncidentId = incidentId }));
        await WaitForAsync(() => hook.Calls.Count == 1); // DOWN failed once, waiting to retry
        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, host.Now.AddMinutes(-10), 1, [webhookId], host.Now)));
        await host.AdvanceUntilAlertsIdleAsync();

        Assert.Equal(2, hook.Calls.Count(m => m.Kind == AlertKind.Down)); // retried and delivered
        Assert.Single(hook.Calls, m => m.Kind == AlertKind.Escalation);
        var notifications = await NotificationsAsync(host);
        var down = Assert.Single(notifications, n => n.Subject.Contains("DOWN") && n.AlertChannelId == webhookId);
        Assert.True(down.Success);
        Assert.Equal(2, down.Attempts);
        Assert.All(notifications, n => Assert.True(n.Success, n.Error));
    }

    [Fact]
    public async Task Waiting_escalation_is_merged_into_a_newer_one_and_the_one_in_delivery_keeps_retrying()
    {
        var hook = new LaneNotifier(AlertChannelType.Webhook, (m, attempt) => m.EscalationLevel == 1 && attempt == 1 ? Transient : NotifyResult.Ok);
        var slack = new LaneNotifier(AlertChannelType.Slack, (_, _) => NotifyResult.Ok);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(hook, slack));
        var (_, monitor, incidentId, webhookId, slackId) = await ArrangeAsync(host);
        var started = host.Now.AddMinutes(-10);

        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, started, 1, [webhookId], host.Now)));
        await WaitForAsync(() => hook.Calls.Count == 1);
        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, started, 2, [webhookId], host.Now)));
        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, started, 3, [slackId], host.Now)));
        await host.AdvanceUntilAlertsIdleAsync();

        Assert.Equal([1, 1, 3], hook.Calls.Select(m => m.EscalationLevel));
        Assert.Equal([3], slack.Calls.Select(m => m.EscalationLevel));
        Assert.All(await NotificationsAsync(host), n => Assert.True(n.Success, n.Error));
        Assert.Equal(0, host.Alerts.Pending);
    }
}
