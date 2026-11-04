using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Scheduling;
using TriggerCheck.Tests.Monitoring;
using TriggerCheck.Web.Heartbeat;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>Heartbeat monitors in the check runner / scheduler: no probe, deadlines, grace, failure pings, incidents and alerts.</summary>
public class HeartbeatEngineTests
{
    private const int Interval = 300;
    private const int Grace = 60;
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);

    // Confirm-before-DOWN is on (production default) to show that heartbeats skip it.
    private Task<MonitoringTestHost> CreateHostAsync(Action<IServiceCollection>? configure = null) =>
        MonitoringTestHost.CreateAsync(s => { s.ReplaceNotifiers(_email); configure?.Invoke(s); },
            new() { ["Monitoring:ConfirmBeforeDown"] = "true", ["Monitoring:ConfirmationDelaySeconds"] = "5" });

    private static async Task<MonitorEntity> AddHeartbeatAsync(MonitoringTestHost host)
    {
        var customer = await host.AddCustomerAsync();
        return await host.AddMonitorAsync(customer.Id, m =>
        {
            m.Type = MonitorType.Heartbeat;
            HeartbeatTokens.Issue(m);
            m.IntervalSeconds = Interval;
            m.GracePeriodSeconds = Grace;
            m.FailureThreshold = 1;
            m.NextCheckAtUtc = HeartbeatSchedule.FirstDeadline(m, host.Now);
        });
    }

    /// <summary>Same update as the /hb endpoint.</summary>
    private static Task PingAsync(MonitoringTestHost host, int id, bool failed = false, string? message = null)
    {
        var now = host.Now;
        return host.WithDbAsync(db => db.Monitors.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.LastHeartbeatAtUtc, now)
            .SetProperty(m => m.LastHeartbeatFailed, failed)
            .SetProperty(m => m.LastHeartbeatMessage, message)
            .SetProperty(m => m.NextCheckAtUtc, now)
            .SetProperty(m => m.Version, m => m.Version + 1)));
    }

    private static Task<List<int>> DueAsync(MonitoringTestHost host) =>
        host.WithDbAsync(db => DueMonitorQuery.SelectDueIdsAsync(db, host.Now, 100, CancellationToken.None));

    [Fact]
    public void Evaluation_rules()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = new MonitorEntity { Type = MonitorType.Heartbeat, IntervalSeconds = Interval, GracePeriodSeconds = Grace };
        Assert.Equal("No heartbeat received yet", HeartbeatMonitorChecker.Evaluate(m, now).Error);

        m.LastHeartbeatAtUtc = now.AddSeconds(-(Interval + Grace - 1));
        Assert.True(HeartbeatMonitorChecker.Evaluate(m, now).IsUp); // last second of the grace period
        m.LastHeartbeatAtUtc = now.AddSeconds(-(Interval + Grace));
        Assert.Equal("No heartbeat for 6m (expected every 5m + 1m grace)", HeartbeatMonitorChecker.Evaluate(m, now).Error);

        m.LastHeartbeatAtUtc = now;
        m.LastHeartbeatFailed = true;
        Assert.Equal("Heartbeat reported a failure", HeartbeatMonitorChecker.Evaluate(m, now).Error);
        m.LastHeartbeatMessage = "backup failed";
        Assert.Equal("Heartbeat reported a failure: backup failed", HeartbeatMonitorChecker.Evaluate(m, now).Error);
    }

    [Fact]
    public async Task Not_due_before_the_first_deadline_then_down_without_confirmation()
    {
        await using var host = await CreateHostAsync();
        var hb = await AddHeartbeatAsync(host);

        host.Time.Advance(TimeSpan.FromSeconds(Interval + Grace - 1));
        Assert.DoesNotContain(hb.Id, await DueAsync(host));

        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains(hb.Id, await DueAsync(host));
        var r = await host.RunCheckAsync(hb.Id);

        Assert.False(r.IsUp);
        Assert.Equal("No heartbeat received yet", r.Error);
        var m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Null(m.PendingConfirmationSinceUtc);                  // grace already was the confirmation
        Assert.Equal(host.Now.AddSeconds(Interval), m.NextCheckAtUtc); // re-evaluated once per interval while overdue
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync(i => i.MonitorId == hb.Id && i.ResolvedAtUtc == null)));
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(AlertKind.Down, alert.Kind);
    }

    [Fact]
    public async Task Pings_keep_it_up_grace_is_honoured_and_a_late_ping_recovers()
    {
        await using var host = await CreateHostAsync();
        var hb = await AddHeartbeatAsync(host);

        host.Time.Advance(TimeSpan.FromSeconds(30));
        await PingAsync(host, hb.Id);
        Assert.Contains(hb.Id, await DueAsync(host)); // a ping makes the monitor due right away
        var pingAt = host.Now;
        Assert.True((await host.RunCheckAsync(hb.Id)).IsUp);
        var m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(pingAt.AddSeconds(Interval + Grace), m.NextCheckAtUtc); // next decision: the deadline

        // Late but within grace: still up.
        host.Time.Advance(TimeSpan.FromSeconds(Interval + Grace - 10));
        Assert.DoesNotContain(hb.Id, await DueAsync(host));
        await PingAsync(host, hb.Id);
        Assert.True((await host.RunCheckAsync(hb.Id)).IsUp);

        // Overdue: DOWN at the deadline.
        host.Time.Advance(TimeSpan.FromSeconds(Interval + Grace));
        Assert.Contains(hb.Id, await DueAsync(host));
        var down = await host.RunCheckAsync(hb.Id);
        Assert.False(down.IsUp);
        Assert.StartsWith("No heartbeat for", down.Error);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(hb.Id)).Status);

        // The job comes back: recovered, incident closed, UP alert.
        host.Time.Advance(TimeSpan.FromSeconds(120));
        await PingAsync(host, hb.Id);
        Assert.True((await host.RunCheckAsync(hb.Id)).IsUp);
        m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync(i => i.ResolvedAtUtc == null)));
        Assert.Equal([AlertKind.Down, AlertKind.Up], _email.Sent.Select(s => s.Message.Kind));
    }

    [Fact]
    public async Task Fail_status_is_an_immediate_failed_check()
    {
        await using var host = await CreateHostAsync();
        var hb = await AddHeartbeatAsync(host);
        await PingAsync(host, hb.Id);
        await host.RunCheckAsync(hb.Id);

        host.Time.Advance(TimeSpan.FromSeconds(10));
        await PingAsync(host, hb.Id, failed: true, message: "exit code 2");
        var r = await host.RunCheckAsync(hb.Id);

        Assert.False(r.IsUp);
        Assert.Equal("Heartbeat reported a failure: exit code 2", r.Error);
        var m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Null(m.PendingConfirmationSinceUtc);
        var incident = await host.WithDbAsync(db => db.Incidents.SingleAsync(i => i.MonitorId == hb.Id));
        Assert.Equal("Heartbeat reported a failure: exit code 2", incident.Cause);

        // A following successful ping recovers it.
        host.Time.Advance(TimeSpan.FromSeconds(10));
        await PingAsync(host, hb.Id);
        Assert.True((await host.RunCheckAsync(hb.Id)).IsUp);
        Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(hb.Id)).Status);
    }

    [Fact]
    public async Task A_ping_arriving_while_the_result_is_saved_is_evaluated_on_the_fresh_row()
    {
        var hook = new SaveHook(db => db.ChangeTracker.Entries<CheckResult>().Any(e => e.State == EntityState.Added));
        await using var host = await CreateHostAsync(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(hook)));
        var hb = await AddHeartbeatAsync(host);
        await PingAsync(host, hb.Id);
        await host.RunCheckAsync(hb.Id); // Up
        host.Time.Advance(TimeSpan.FromSeconds(Interval + Grace)); // overdue: the snapshot says DOWN
        hook.Action = () => PingAsync(host, hb.Id);                 // ... but the job pings while the result is saved

        var r = await host.RunCheckAsync(hb.Id);

        Assert.True(hook.Fired);
        Assert.True(r.IsUp);                                        // decided on the fresh row
        var m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(host.Now.AddSeconds(Interval + Grace), m.NextCheckAtUtc);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);                                  // no DOWN alert for a ping that did arrive
    }

    [Fact]
    public async Task Frequent_pings_store_an_unchanged_up_at_most_once_per_interval()
    {
        await using var host = await CreateHostAsync();
        var hb = await AddHeartbeatAsync(host);
        for (var i = 0; i < 20; i++)
        {
            await PingAsync(host, hb.Id);
            Assert.True((await host.RunCheckAsync(hb.Id)).IsUp);
            host.Time.Advance(TimeSpan.FromSeconds(Interval / 10));
        }
        // 20 evaluations over two intervals: Unknown->Up, then one per interval.
        var rows = await host.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == hb.Id));
        Assert.InRange(rows, 2, 3);

        // A failure is always stored.
        await PingAsync(host, hb.Id, failed: true);
        await host.RunCheckAsync(hb.Id);
        Assert.Equal(rows + 1, await host.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == hb.Id)));
    }

    [Fact]
    public void A_ping_makes_the_monitor_due_only_when_there_is_something_to_decide()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        HeartbeatEndpoint.PingTarget Target(MonitorStatus status, DateTime? lastChecked, bool lastFailed = false, DateTime? next = null) =>
            new(1, status, true, Interval, Grace, lastChecked, lastFailed, next);

        Assert.Equal(now, HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Unknown, null), false, now));          // first ping
        Assert.Equal(now, HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Down, now.AddSeconds(-1)), false, now)); // recovery
        Assert.Equal(now, HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Up, now.AddSeconds(-1)), true, now));    // failure
        Assert.Equal(now, HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Up, now.AddSeconds(-1), lastFailed: true), false, now));
        Assert.Equal(now, HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Up, now.AddSeconds(-Interval / 10)), false, now)); // N per interval

        // Up and evaluated moments ago: only the deadline moves.
        Assert.Equal(now.AddSeconds(Interval + Grace), HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Up, now.AddSeconds(-5), next: now.AddMinutes(3)), false, now));
        // ... unless an evaluation is already due.
        Assert.Equal(now.AddSeconds(-2), HeartbeatEndpoint.NextCheckAfterPing(Target(MonitorStatus.Up, now.AddSeconds(-5), next: now.AddSeconds(-2)), false, now));
    }

    [Fact]
    public async Task Resume_gives_a_full_window_and_ignores_pings_from_before_the_pause()
    {
        await using var host = await CreateHostAsync();
        var hb = await AddHeartbeatAsync(host);
        await PingAsync(host, hb.Id);
        await host.RunCheckAsync(hb.Id);

        await host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().PauseAsync(hb.Id, null));
        host.Time.Advance(TimeSpan.FromDays(2));
        await host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().ResumeAsync(hb.Id, null));

        var m = await host.GetMonitorAsync(hb.Id);
        Assert.Equal(host.Now.AddSeconds(Interval + Grace), m.NextCheckAtUtc);
        Assert.DoesNotContain(hb.Id, await DueAsync(host));

        host.Time.Advance(TimeSpan.FromSeconds(Interval + Grace));
        Assert.False((await host.RunCheckAsync(hb.Id)).IsUp); // the old ping does not count
    }

    [Fact]
    public void Schedule_helpers()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var m = new MonitorEntity { Type = MonitorType.Heartbeat, IntervalSeconds = 60, GracePeriodSeconds = 30, Status = MonitorStatus.Up };
        Assert.Equal(now.AddSeconds(90), HeartbeatSchedule.FirstDeadline(m, now));
        Assert.Equal(now.AddSeconds(60), HeartbeatSchedule.NextEvaluation(m, now)); // never pinged
        m.LastHeartbeatAtUtc = now.AddSeconds(-10);
        Assert.Equal(now.AddSeconds(80), HeartbeatSchedule.NextEvaluation(m, now));
        Assert.Equal(now, HeartbeatSchedule.AfterSettingsChange(m, becameHeartbeat: false, now));
        Assert.Equal(now.AddSeconds(90), HeartbeatSchedule.AfterSettingsChange(m, becameHeartbeat: true, now));
    }
}
