using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>ENG-9: a failure that would take a monitor DOWN is confirmed by a second probe after a delay.</summary>
public class ConfirmBeforeAlertTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Fail = CheckOutcome.Down("Connection refused", 3);
    private const int DelaySeconds = 5;

    private readonly RecordingNotifier _email = new(AlertChannelType.Email);
    private TimedChecker _checker = null!;

    private async Task<MonitoringTestHost> CreateHostAsync()
    {
        var host = await MonitoringTestHost.CreateAsync(
            s => s.ReplaceCheckers().AddSingleton<IMonitorChecker>(sp => _checker).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = "true", ["Monitoring:ConfirmationDelaySeconds"] = DelaySeconds.ToString() });
        _checker = new TimedChecker(host.Time);
        return host;
    }

    /// <summary>
    /// Runs one check. Fake time is only advanced after the first probe was taken (so its timestamp is exact),
    /// in 1 s steps while the runner waits for its confirmation delay.
    /// </summary>
    private async Task<CheckResult> RunAsync(MonitoringTestHost host, int monitorId, Func<Task>? afterFirstProbe = null)
    {
        var callsBefore = _checker.Calls.Count;
        var task = host.RunCheckAsync(monitorId);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_checker.Calls.Count == callsBefore && !task.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(5);
        if (afterFirstProbe is not null) await afterFirstProbe();
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < limit)
        {
            await Task.WhenAny(task, Task.Delay(20));
            if (!task.IsCompleted) host.Time.Advance(TimeSpan.FromSeconds(1));
        }
        return await task;
    }

    [Fact]
    public async Task Single_transient_blip_does_not_open_an_incident()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.IntervalSeconds = 60; });
        _checker.Then(Ok, Fail, Ok);

        await RunAsync(host, monitor.Id);
        host.Time.Advance(TimeSpan.FromSeconds(60));
        var failedAt = host.Now;
        var latest = await RunAsync(host, monitor.Id);

        Assert.True(latest.IsUp); // the confirmation is the latest word
        Assert.Equal(3, _checker.Calls.Count);
        Assert.True(_checker.Calls[2] - _checker.Calls[1] >= TimeSpan.FromSeconds(DelaySeconds));
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.LastError);
        Assert.Equal(failedAt.AddSeconds(60), m.NextCheckAtUtc); // schedule continues from the original probe
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);

        // Both probes are kept in the history.
        var results = await host.WithDbAsync(db => db.CheckResults.Where(c => c.MonitorId == monitor.Id).OrderBy(c => c.CheckedAtUtc).ToListAsync());
        Assert.Equal([true, false, true], results.Select(r => r.IsUp));
    }

    [Fact]
    public async Task Real_outage_opens_an_incident_within_interval_plus_delay()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 2; m.IntervalSeconds = 30; });
        _checker.Then(Ok, Fail, Fail, Fail);

        await RunAsync(host, monitor.Id);           // Up
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var outageStart = host.Now;
        await RunAsync(host, monitor.Id);           // 1st failure: below threshold, no confirmation
        Assert.Equal(2, _checker.Calls.Count);
        Assert.Equal(1, (await host.GetMonitorAsync(monitor.Id)).ConsecutiveFailures);

        host.Time.Advance(TimeSpan.FromSeconds(30));
        var failedAt = host.Now;
        await RunAsync(host, monitor.Id);           // 2nd failure + failed confirmation => DOWN
        Assert.Equal(4, _checker.Calls.Count);

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(3, m.ConsecutiveFailures);
        Assert.Equal(failedAt, m.StatusChangedAtUtc);
        var incident = await host.WithDbAsync(db => db.Incidents.SingleAsync());
        Assert.Equal(failedAt, incident.StartedAtUtc);
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(AlertKind.Down, alert.Kind);

        // Detected (confirmation finished) within one interval + delay (+ fake-clock stepping slack) of the 2nd failing probe.
        var detectedAt = _checker.Calls[3];
        Assert.True(detectedAt - failedAt >= TimeSpan.FromSeconds(DelaySeconds));
        Assert.True(detectedAt - outageStart <= TimeSpan.FromSeconds(30 + DelaySeconds + 10), $"detected after {detectedAt - outageStart}");

        // Already down: further failures are not confirmed again.
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await RunAsync(host, monitor.Id);
        Assert.Equal(5, _checker.Calls.Count);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Pause_during_confirmation_records_the_failure_without_transition()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Fail, Fail);

        await RunAsync(host, monitor.Id, afterFirstProbe: () =>
            host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().PauseAsync(monitor.Id, customer.Id)));

        Assert.Single(_checker.Calls); // no confirmation probe for a paused monitor
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal("Connection refused", m.LastError);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Delete_during_confirmation_discards_the_result()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Fail, Fail);

        var ex = await Assert.ThrowsAsync<MonitorCheckDiscardedException>(() => RunAsync(host, monitor.Id, afterFirstProbe: () =>
            host.WithDbAsync(async db => { await db.Monitors.Where(m => m.Id == monitor.Id).ExecuteDeleteAsync(); })));
        Assert.True(ex.MonitorDeleted);
        Assert.Single(_checker.Calls);
        Assert.Equal(0, await host.WithDbAsync(db => db.CheckResults.CountAsync()));
    }

    [Fact]
    public async Task Probe_settings_edited_during_confirmation_discard_the_result()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Fail, Fail);

        var ex = await Assert.ThrowsAsync<MonitorCheckDiscardedException>(() => RunAsync(host, monitor.Id, afterFirstProbe: () =>
            host.WithDbAsync(async db =>
            {
                var m = await db.Monitors.SingleAsync(x => x.Id == monitor.Id);
                m.Target = "http://127.0.0.2/";
                m.Version++;
                await db.SaveChangesAsync();
            })));
        Assert.False(ex.MonitorDeleted);
        Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(monitor.Id)).Status);
    }

    [Fact]
    public async Task Disabled_option_goes_down_without_confirmation()
    {
        var checker = new ScriptedChecker(MonitorType.Http).Then(Fail);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = "false" });
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);

        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(1, checker.Calls);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
    }

    // ---- pure state machine rules ----

    private static MonitorEntity Active(MonitorStatus status, int failures, int threshold) =>
        new() { Status = status, ConsecutiveFailures = failures, FailureThreshold = threshold, IntervalSeconds = 60, IsEnabled = true };

    [Theory]
    [InlineData(MonitorStatus.Up, 0, 1, true)]
    [InlineData(MonitorStatus.Up, 0, 2, false)]
    [InlineData(MonitorStatus.Up, 1, 2, true)]
    [InlineData(MonitorStatus.Unknown, 2, 3, true)]
    [InlineData(MonitorStatus.Down, 5, 2, false)]
    [InlineData(MonitorStatus.Paused, 1, 1, false)]
    public void NeedsConfirmation_only_when_the_failure_would_go_down(MonitorStatus status, int failures, int threshold, bool expected)
    {
        Assert.Equal(expected, MonitorStateMachine.NeedsConfirmation(Active(status, failures, threshold), Fail));
        Assert.False(MonitorStateMachine.NeedsConfirmation(Active(status, failures, threshold), Ok));
    }

    [Fact]
    public void ApplyConfirmed_counts_both_failures_and_dates_the_outage_from_the_first()
    {
        var m = Active(MonitorStatus.Up, 1, 2);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var transition = MonitorStateMachine.ApplyConfirmed(m, Fail, t0, CheckOutcome.Down("Timeout", 0), t0.AddSeconds(5));
        Assert.Equal(StatusTransition.WentDown, transition);
        Assert.Equal(3, m.ConsecutiveFailures);
        Assert.Equal(t0, m.StatusChangedAtUtc);
        Assert.Equal(t0.AddSeconds(5), m.LastCheckedAtUtc);
        Assert.Equal("Timeout", m.LastError);
        Assert.Equal(t0.AddSeconds(60), m.NextCheckAtUtc);
    }

    [Fact]
    public void ApplyConfirmed_transient_resets_the_counter_and_unknown_becomes_up()
    {
        var m = Active(MonitorStatus.Unknown, 2, 3);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(StatusTransition.BecameUp, MonitorStateMachine.ApplyConfirmed(m, Fail, t0, Ok, t0.AddSeconds(5)));
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Null(m.LastError);
    }

    /// <summary>Scripted checker that records the (fake) time of each call.</summary>
    private sealed class TimedChecker(TimeProvider time) : IMonitorChecker
    {
        private readonly ConcurrentQueue<CheckOutcome> _queue = new();
        private CheckOutcome _last = Ok;
        private readonly List<DateTime> _calls = [];
        public IReadOnlyList<DateTime> Calls { get { lock (_calls) return _calls.ToList(); } }
        public MonitorType Type => MonitorType.Http;

        public TimedChecker Then(params CheckOutcome[] outcomes)
        {
            foreach (var o in outcomes) _queue.Enqueue(o);
            return this;
        }

        public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
        {
            lock (_calls) _calls.Add(time.GetUtcNow().UtcDateTime);
            if (_queue.TryDequeue(out var o)) _last = o;
            return Task.FromResult(_last);
        }
    }
}
