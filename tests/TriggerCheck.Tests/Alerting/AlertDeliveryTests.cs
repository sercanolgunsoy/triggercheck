using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Alerting;

/// <summary>ENG-4 (queue, timeout, retries, shutdown) and ENG-6 (no-channel fallback) on the production DI wiring.</summary>
public class AlertDeliveryTests
{
    private static readonly CheckOutcome Fail = CheckOutcome.Down("Connection refused", 5);

    private static AlertMessage Down(int monitorId, int customerId) =>
        new(monitorId, customerId, "Shop", "https://shop.example.test", AlertKind.Down, "Connection refused", MonitoringTestHost.Start.UtcDateTime);

    private static Task AddChannelAsync(MonitoringTestHost host, int customerId, AlertChannelType type, string target, bool enabled = true) =>
        host.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = customerId, Name = type.ToString(), Type = type, Target = target, IsEnabled = enabled });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Hanging_notifier_does_not_delay_checks_and_times_out_with_retries()
    {
        var hanging = new ScriptedNotifier(AlertChannelType.Email, (_, _) => new TaskCompletionSource<NotifyResult>().Task); // ignores its token
        var checker = new ScriptedChecker(MonitorType.Http).Then(Fail);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(hanging));
        var customer = await host.AddCustomerAsync();
        await AddChannelAsync(host, customer.Id, AlertChannelType.Email, "ops@example.test");
        var a = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);
        var b = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);

        var sw = Stopwatch.StartNew();
        await RunAsync(host, a.Id);          // goes DOWN -> alert queued, sender hangs on it
        await RunAsync(host, b.Id);          // goes DOWN too
        for (var i = 0; i < 5; i++) await RunAsync(host, b.Id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"checks took {sw.Elapsed}");
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(a.Id)).Status);
        Assert.Equal(2, host.Alerts.Pending);

        await host.AdvanceUntilAlertsIdleAsync();

        var notifications = await host.WithDbAsync(db => db.Notifications.ToListAsync());
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, n =>
        {
            Assert.False(n.Success);
            Assert.Equal("Timeout after 30s", n.Error);
            Assert.Equal(AlertingOptions.MaxAttempts, n.Attempts);
        });
        Assert.Equal(2 * AlertingOptions.MaxAttempts, hanging.Calls.Count);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_exponential_backoff()
    {
        var calls = 0;
        var flaky = new ScriptedNotifier(AlertChannelType.Webhook, (_, _) =>
            Task.FromResult(Interlocked.Increment(ref calls) <= 2 ? NotifyResult.Transient("Webhook returned HTTP 503") : NotifyResult.Ok));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(flaky));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");
        flaky.Time = host.Time;

        Assert.True(host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id)));
        await host.AdvanceUntilAlertsIdleAsync();

        var times = flaky.Calls.Select(c => c.At).ToList();
        Assert.Equal(3, times.Count);
        Assert.InRange(times[1] - times[0], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6));
        Assert.InRange(times[2] - times[1], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31));
        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.True(n.Success);
        Assert.Null(n.Error);
        Assert.Equal(3, n.Attempts);
    }

    [Fact]
    public async Task Final_failure_is_recorded_after_all_attempts()
    {
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _) => Task.FromResult(NotifyResult.Transient("Webhook returned HTTP 500")));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(failing));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");
        var start = host.Now;

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        await host.AdvanceUntilAlertsIdleAsync();

        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.False(n.Success);
        Assert.Equal("Webhook returned HTTP 500", n.Error);
        Assert.Equal(4, n.Attempts);
        Assert.True(n.SentAtUtc - start >= TimeSpan.FromSeconds(5 + 30 + 120), $"recorded at +{n.SentAtUtc - start}");
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _) => Task.FromResult(NotifyResult.Fail("Webhook returned HTTP 404")));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(failing));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        await host.AlertsIdleAsync();

        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.Equal(1, n.Attempts);
        Assert.Single(failing.Calls);
    }

    [Fact]
    public async Task Shutdown_ends_retry_waits_records_the_failure_and_refuses_new_alerts()
    {
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _) => Task.FromResult(NotifyResult.Transient("Webhook returned HTTP 503")));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(failing));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        await WaitUntilAsync(() => failing.Calls.Count == 1); // now waiting 5s (fake time) for the retry

        await host.Services.GetRequiredService<AlertSenderService>().StopAsync(CancellationToken.None);

        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.False(n.Success);
        Assert.Equal(1, n.Attempts);
        Assert.Equal(0, host.Alerts.Pending);
        Assert.False(host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id)));
    }

    [Fact]
    public async Task Check_now_alerts_are_delivered_with_the_scheduler_disabled()
    {
        // MonitoringTestHost runs with Monitoring:Enabled=false, like "check now" in a web-only deployment.
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(new ScriptedChecker(MonitorType.Http).Then(Fail)).ReplaceNotifiers(email));
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);

        await host.RunCheckAsync(monitor.Id);

        Assert.Equal("boss@example.test", Assert.Single(email.Sent).Target);
        Assert.Equal(1, (await host.WithDbAsync(db => db.Notifications.SingleAsync())).Attempts);
    }

    // ---- ENG-6 ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_enabled_channel_and_fallback_off_sends_nothing()
    {
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(email));
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Email, "ops@example.test", enabled: false);
        await host.WithDbAsync(db => db.Customers.Where(c => c.Id == customer.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AlertContactEmailWhenNoChannel, false)));

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Empty(result);
        Assert.Empty(email.Sent);
        Assert.Equal(0, await host.WithDbAsync(db => db.Notifications.CountAsync()));
    }

    [Fact]
    public async Task No_enabled_channel_and_fallback_on_uses_contact_email()
    {
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(email));
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        Assert.True(customer.AlertContactEmailWhenNoChannel); // default
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Email, "ops@example.test", enabled: false);

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Equal("boss@example.test", Assert.Single(email.Sent).Target);
    }

    // ---- FEAT-7 plan gate -----------------------------------------------------------------------------------

    [Fact]
    public async Task Sms_channel_is_not_delivered_when_the_plan_does_not_allow_it()
    {
        var sms = new RecordingNotifier(AlertChannelType.Sms);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(sms));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Sms, "+905551234567");

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));
        Assert.Empty(sms.Sent);
        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.False(n.Success);
        Assert.Equal(AlertDispatcher.NotInPlanError, n.Error);
        Assert.Equal(0, n.Attempts);

        await host.WithDbAsync(db => db.Plans.ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowsSmsVoice, true)));
        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));
        Assert.Equal("+905551234567", Assert.Single(sms.Sent).Target);
    }

    private static Task RunAsync(MonitoringTestHost host, int monitorId) =>
        host.InScopeAsync(sp => sp.GetRequiredService<IMonitorCheckRunner>().RunAsync(monitorId));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    /// <summary>Notifier with a scripted behavior that records each call with the (fake) time it happened.</summary>
    private sealed class ScriptedNotifier(AlertChannelType type, Func<NotifyTarget, CancellationToken, Task<NotifyResult>> behavior) : INotifier
    {
        public TimeProvider? Time { get; set; }
        public ConcurrentQueue<(string Target, DateTimeOffset At)> Calls { get; } = new();
        public AlertChannelType Type => type;

        public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
        {
            Calls.Enqueue((target.Address, Time?.GetUtcNow() ?? default));
            return behavior(target, ct);
        }
    }
}
