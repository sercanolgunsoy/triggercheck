using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Tests.Monitoring;

public class MonitorCheckRunnerTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Fail = CheckOutcome.Down("Connection refused", 3);

    private readonly ScriptedChecker _checker = new(MonitorType.Http);
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);
    private readonly RecordingNotifier _webhook = new(AlertChannelType.Webhook, t => t.Contains("broken") ? NotifyResult.Fail("HTTP 500") : NotifyResult.Ok);

    private Task<MonitoringTestHost> CreateHostAsync() =>
        MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(_checker).ReplaceNotifiers(_email, _webhook));

    private static Task AddChannelsAsync(MonitoringTestHost host, int customerId) =>
        host.WithDbAsync(async db =>
        {
            db.AlertChannels.AddRange(
                new AlertChannel { CustomerId = customerId, Name = "mail", Type = AlertChannelType.Email, Target = "ops@example.test" },
                new AlertChannel { CustomerId = customerId, Name = "hook", Type = AlertChannelType.Webhook, Target = "http://hooks.example.test/broken" },
                new AlertChannel { CustomerId = customerId, Name = "off", Type = AlertChannelType.Email, Target = "off@example.test", IsEnabled = false });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Full_down_and_recovery_cycle_opens_and_closes_incident_and_alerts_once_per_transition()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        await AddChannelsAsync(host, customer.Id);
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 2; m.IntervalSeconds = 30; });
        _checker.Then(Ok, Fail, Fail, Fail, Ok, Ok);

        // 1: Unknown -> Up, no alert
        var r1 = await host.RunCheckAsync(monitor.Id);
        Assert.True(r1.IsUp);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(host.Now.AddSeconds(30), m.NextCheckAtUtc);
        Assert.Empty(_email.Sent);

        // 2: first failure, still Up
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await host.RunCheckAsync(monitor.Id);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(1, m.ConsecutiveFailures);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));

        // 3: threshold reached -> Down, incident, DOWN alert to both enabled channels
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var downAt = host.Now;
        await host.RunCheckAsync(monitor.Id);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(downAt, m.StatusChangedAtUtc);
        Assert.Equal("Connection refused", m.LastError);
        var incident = await host.WithDbAsync(db => db.Incidents.SingleAsync());
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Equal(downAt, incident.StartedAtUtc);
        Assert.Equal("Connection refused", incident.Cause);
        Assert.Single(_email.Sent);
        Assert.Single(_webhook.Sent);
        var (to, msg) = Assert.Single(_email.Sent);
        Assert.Equal("ops@example.test", to);
        Assert.Equal(AlertKind.Down, msg.Kind);
        Assert.Equal("Connection refused", msg.Error);

        // 4: still failing -> no new incident, no new alert
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Single(_email.Sent);

        // 5: recovery -> Up, incident closed, UP alert
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var upAt = host.Now;
        await host.RunCheckAsync(monitor.Id);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.LastError);
        incident = await host.WithDbAsync(db => db.Incidents.SingleAsync());
        Assert.Equal(upAt, incident.ResolvedAtUtc);
        Assert.Equal(2, _email.Sent.Count);
        var up = _email.Sent.Last().Message;
        Assert.Equal(AlertKind.Up, up.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), up.Downtime);

        // 6: stays up -> nothing new
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(2, _email.Sent.Count);
        Assert.Equal(2, _webhook.Sent.Count);

        Assert.Equal(6, await host.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == monitor.Id)));

        // Every alert attempt is recorded, including failed webhook deliveries.
        var notifications = await host.WithDbAsync(db => db.Notifications.OrderBy(n => n.Id).ToListAsync());
        Assert.Equal(4, notifications.Count);
        Assert.All(notifications, n => Assert.NotNull(n.AlertChannelId));
        Assert.Equal(2, notifications.Count(n => !n.Success && n.Error == "HTTP 500"));
        Assert.Contains(notifications, n => n.Subject.Contains("DOWN"));
        Assert.Contains(notifications, n => n.Subject.Contains("UP"));
    }

    [Fact]
    public async Task Unknown_failures_below_threshold_stay_unknown()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 3);
        _checker.Then(Fail);

        await host.RunCheckAsync(monitor.Id);
        await host.RunCheckAsync(monitor.Id);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Unknown, m.Status);
        Assert.Equal(2, m.ConsecutiveFailures);
        Assert.Empty(_email.Sent);

        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
    }

    [Fact]
    public async Task Without_alert_channels_contact_email_is_used()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync(contactEmail: "boss@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);
        _checker.Then(Fail);

        await host.RunCheckAsync(monitor.Id);

        var (to, _) = Assert.Single(_email.Sent);
        Assert.Equal("boss@example.test", to);
        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.Null(n.AlertChannelId);
        Assert.True(n.Success);
    }

    [Theory]
    [InlineData(MonitorStatus.Paused, true, true)]
    [InlineData(MonitorStatus.Up, false, true)]   // disabled
    [InlineData(MonitorStatus.Up, true, false)]   // customer suspended
    public async Task Check_now_on_inactive_monitor_records_result_but_keeps_status(MonitorStatus status, bool enabled, bool customerActive)
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync(isActive: customerActive);
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.Status = status; m.IsEnabled = enabled; m.FailureThreshold = 1; });
        _checker.Then(Fail);

        var result = await host.RunCheckAsync(monitor.Id);

        Assert.False(result.IsUp);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(status, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.NextCheckAtUtc);
        Assert.Equal(host.Now, m.LastCheckedAtUtc);
        Assert.Equal("Connection refused", m.LastError);
        Assert.Equal(1, await host.WithDbAsync(db => db.CheckResults.CountAsync()));
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Throwing_checker_and_notifier_do_not_break_the_pipeline()
    {
        var throwingNotifier = new ThrowingNotifier();
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(new ThrowingChecker()).ReplaceNotifiers(throwingNotifier));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);

        var result = await host.RunCheckAsync(monitor.Id);

        Assert.False(result.IsUp);
        Assert.Equal("Internal checker error", result.Error); // exception details stay in the log
        Assert.DoesNotContain("boom", (await host.GetMonitorAsync(monitor.Id)).LastError);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        var n = await host.WithDbAsync(db => db.Notifications.SingleAsync());
        Assert.False(n.Success);
        Assert.Contains("smtp exploded", n.Error);
    }

    [Fact]
    public async Task Unknown_monitor_id_throws_KeyNotFound()
    {
        await using var host = await CreateHostAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.RunCheckAsync(12345));
    }

    [Fact]
    public async Task Concurrent_runs_of_same_monitor_open_only_one_incident()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);
        _checker.Then(Fail);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => host.RunCheckAsync(monitor.Id)));

        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Single(_email.Sent);
        Assert.Equal(5, (await host.GetMonitorAsync(monitor.Id)).ConsecutiveFailures);
    }

    private sealed class ThrowingChecker : IMonitorChecker
    {
        public MonitorType Type => MonitorType.Http;
        public Task<CheckOutcome> CheckAsync(Domain.Entities.Monitor monitor, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingNotifier : INotifier
    {
        public AlertChannelType Type => AlertChannelType.Email;
        public Task<NotifyResult> SendAsync(string target, AlertMessage message, CancellationToken ct) => throw new InvalidOperationException("smtp exploded");
    }
}
