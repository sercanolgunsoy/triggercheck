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
        var hanging = new ScriptedNotifier(AlertChannelType.Email, (_, _, _) => new TaskCompletionSource<NotifyResult>().Task); // ignores its token
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
        var flaky = new ScriptedNotifier(AlertChannelType.Webhook, (_, _, _) =>
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
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _, _) => Task.FromResult(NotifyResult.Transient("Webhook returned HTTP 500")));
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
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _, _) => Task.FromResult(NotifyResult.Fail("Webhook returned HTTP 404")));
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
        var failing = new ScriptedNotifier(AlertChannelType.Webhook, (_, _, _) => Task.FromResult(NotifyResult.Transient("Webhook returned HTTP 503")));
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

    private static readonly Dictionary<string, string?> Twilio = new()
    {
        ["Twilio:AccountSid"] = "AC123", ["Twilio:AuthToken"] = "token", ["Twilio:FromNumber"] = "+15550001111"
    };

    [Fact]
    public async Task Undeliverable_sms_channel_is_recorded_and_falls_back_to_contact_email()
    {
        var sms = new RecordingNotifier(AlertChannelType.Sms);
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(sms, email), Twilio);
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Sms, "+905551234567");

        // Plan without SMS: the SMS row records why, and the alert still reaches the contact e-mail (ENG-6 setting on).
        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));
        Assert.Empty(sms.Sent);
        Assert.Equal("boss@example.test", Assert.Single(email.Sent).Target);
        var rows = await host.WithDbAsync(db => db.Notifications.ToListAsync());
        var smsRow = Assert.Single(rows, n => n.AlertChannelId != null);
        Assert.False(smsRow.Success);
        Assert.Equal(AlertDispatcher.NotInPlanError, smsRow.Error);
        Assert.Equal(0, smsRow.Attempts);

        // Plan allows SMS: delivered, no fallback.
        await host.WithDbAsync(db => db.Plans.ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowsSmsVoice, true)));
        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));
        Assert.Equal("+905551234567", Assert.Single(sms.Sent).Target);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task Sms_without_twilio_configuration_counts_as_undeliverable()
    {
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(new RecordingNotifier(AlertChannelType.VoiceCall), email));
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id);
        await host.WithDbAsync(db => db.Plans.ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowsSmsVoice, true)));
        await AddChannelAsync(host, customer.Id, AlertChannelType.VoiceCall, "+905551234567");

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Single(email.Sent);
        Assert.Contains(await host.WithDbAsync(db => db.Notifications.Select(n => n.Error).ToListAsync()), e => e == TwilioNotifier.NotConfiguredError);
    }

    [Fact]
    public async Task Daily_sms_voice_limit_is_enforced_per_customer()
    {
        var sms = new RecordingNotifier(AlertChannelType.Sms);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(sms),
            new(Twilio) { ["Alerting:SmsVoiceDailyLimitPerCustomer"] = "2" });
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await host.WithDbAsync(db => db.Plans.ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowsSmsVoice, true)));
        await AddChannelAsync(host, customer.Id, AlertChannelType.Sms, "+905551234567");

        for (var i = 0; i < 3; i++)
            await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Equal(2, sms.Sent.Count);
        var last = await host.WithDbAsync(db => db.Notifications.OrderByDescending(n => n.Id).FirstAsync());
        Assert.False(last.Success);
        Assert.Equal(AlertDispatcher.DailyLimitError, last.Error);
        Assert.Equal(0, last.Attempts);

        host.Time.Advance(TimeSpan.FromDays(1)); // next UTC day
        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));
        Assert.Equal(3, sms.Sent.Count);
    }

    [Fact]
    public async Task Non_idempotent_channel_is_not_retried_after_a_timeout()
    {
        var hanging = new ScriptedNotifier(AlertChannelType.Webhook, (_, _, _) => new TaskCompletionSource<NotifyResult>().Task) { Idempotent = false };
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(hanging));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        await host.AdvanceUntilAlertsIdleAsync();

        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.Equal("Timeout after 30s", n.Error);
        Assert.Equal(1, n.Attempts);
    }

    [Fact]
    public async Task Alerts_of_deleted_monitors_are_not_recorded()
    {
        var email = new RecordingNotifier(AlertChannelType.Email);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(email));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await host.WithDbAsync(db => db.Monitors.Where(m => m.Id == monitor.Id).ExecuteDeleteAsync());

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Single(email.Sent);
        Assert.Equal(0, await host.WithDbAsync(db => db.Notifications.CountAsync()));
    }

    // ---- ordering and fairness ----------------------------------------------------------------------------

    [Fact]
    public async Task Retrying_down_is_superseded_by_the_following_up_and_never_arrives_last()
    {
        var notifier = new ScriptedNotifier(AlertChannelType.Webhook, (_, m, _) =>
            Task.FromResult(m.Kind == AlertKind.Down ? NotifyResult.Transient("Webhook returned HTTP 503") : NotifyResult.Ok));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(notifier));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        await WaitUntilAsync(() => notifier.Calls.Count == 1);       // DOWN failed, now waiting 5s (fake) to retry
        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id) with { Kind = AlertKind.Up, Error = null });
        await host.AlertsIdleAsync();                                // no fake time advanced: the retry wait was cut short

        Assert.Equal([AlertKind.Down, AlertKind.Up], notifier.Calls.Select(c => c.Kind));
        var rows = await host.WithDbAsync(db => db.Notifications.OrderBy(n => n.Id).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.StartsWith(AlertDispatcher.SupersededError, rows[0].Error);
        Assert.Equal(1, rows[0].Attempts);
        Assert.True(rows[1].Success);
        Assert.Equal("[TriggerCheck] UP: Shop", rows[1].Subject);
    }

    [Fact]
    public async Task Alerts_of_one_monitor_are_delivered_one_at_a_time_in_order()
    {
        var release = new TaskCompletionSource<NotifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new ScriptedNotifier(AlertChannelType.Webhook, (_, m, _) => m.Kind == AlertKind.Down ? release.Task : Task.FromResult(NotifyResult.Ok));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(notifier));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");

        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id));
        host.Alerts.TryEnqueue(Down(monitor.Id, customer.Id) with { Kind = AlertKind.Up, Error = null });
        await WaitUntilAsync(() => notifier.Calls.Count == 1);
        await Task.Delay(200);
        Assert.Single(notifier.Calls); // UP waits for the DOWN delivery to finish

        release.SetResult(NotifyResult.Ok);
        await host.AlertsIdleAsync();
        Assert.Equal([AlertKind.Down, AlertKind.Up], notifier.Calls.Select(c => c.Kind));
    }

    [Fact]
    public async Task A_customer_with_a_broken_endpoint_does_not_delay_other_customers()
    {
        var notifier = new ScriptedNotifier(AlertChannelType.Webhook, (t, _, _) => t.Address.Contains("broken")
            ? new TaskCompletionSource<NotifyResult>().Task   // hangs until the 30s (fake) timeout, then retried
            : Task.FromResult(NotifyResult.Ok));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(notifier));
        var noisy = await host.AddCustomerAsync();
        var quiet = await host.AddCustomerAsync();
        await AddChannelAsync(host, noisy.Id, AlertChannelType.Webhook, "https://broken.example.test/x");
        await AddChannelAsync(host, quiet.Id, AlertChannelType.Webhook, "https://ok.example.test/x");
        for (var i = 0; i < 30; i++)
        {
            var m = await host.AddMonitorAsync(noisy.Id);
            host.Alerts.TryEnqueue(Down(m.Id, noisy.Id));
        }
        await WaitUntilAsync(() => notifier.Calls.Count >= 3); // noisy customer saturated its own cap

        var quietMonitor = await host.AddMonitorAsync(quiet.Id);
        host.Alerts.TryEnqueue(Down(quietMonitor.Id, quiet.Id));

        // Delivered without any (fake) time passing, i.e. without waiting for a single noisy attempt to time out.
        await WaitUntilAsync(() => notifier.Calls.Any(c => c.Target.Contains("ok.example")));
        Assert.Equal(3, notifier.Calls.Count(c => c.Target.Contains("broken"))); // per-customer cap: 3 attempts at a time
        Assert.Equal(MonitoringTestHost.Start, host.Time.GetUtcNow());

        await host.AdvanceUntilAlertsIdleAsync(maxSimulated: TimeSpan.FromHours(2));
        Assert.True(await host.WithDbAsync(db => db.Notifications.AnyAsync(n => n.MonitorId == quietMonitor.Id && n.Success)));
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

    /// <summary>Notifier with a scripted behavior that records each call (target, alert kind, fake time).</summary>
    private sealed class ScriptedNotifier(AlertChannelType type, Func<NotifyTarget, AlertMessage, CancellationToken, Task<NotifyResult>> behavior) : INotifier
    {
        public TimeProvider? Time { get; set; }
        public bool Idempotent { get; init; } = true;
        public ConcurrentQueue<(string Target, AlertKind Kind, DateTimeOffset At)> Calls { get; } = new();
        public AlertChannelType Type => type;
        public bool IsIdempotent => Idempotent;

        public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
        {
            Calls.Enqueue((target.Address, message.Kind, Time?.GetUtcNow() ?? default));
            return behavior(target, message, ct);
        }
    }
}
