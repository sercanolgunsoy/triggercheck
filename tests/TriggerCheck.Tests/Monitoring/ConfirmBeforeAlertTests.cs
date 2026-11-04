using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Scheduling;
using TriggerCheck.Monitoring.Stats;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>
/// ENG-9: a failure that would take a monitor DOWN is recorded and confirmed by a second probe on a later run
/// (scheduled ConfirmationDelaySeconds later); no run ever waits for the delay.
/// </summary>
public class ConfirmBeforeAlertTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Refused = CheckOutcome.Down("Connection refused", 3);
    private static readonly CheckOutcome Timeout = CheckOutcome.Down("Timeout after 10s", 10000);
    private const int DelaySeconds = 5;

    private readonly ScriptedChecker _checker = new(MonitorType.Http);
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);

    private Task<MonitoringTestHost> CreateHostAsync(Action<IServiceCollection>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Monitoring:ConfirmBeforeDown"] = "true",
            ["Monitoring:ConfirmationDelaySeconds"] = DelaySeconds.ToString()
        };
        foreach (var kv in settings ?? new()) all[kv.Key] = kv.Value;
        return MonitoringTestHost.CreateAsync(s => { s.ReplaceCheckers(_checker).ReplaceNotifiers(_email); configure?.Invoke(s); }, all);
    }

    private static Task<List<CheckResult>> ResultsAsync(MonitoringTestHost host, int monitorId) =>
        host.WithDbAsync(db => db.CheckResults.Where(c => c.MonitorId == monitorId).OrderBy(c => c.Id).ToListAsync());

    [Fact]
    public async Task Single_transient_blip_does_not_open_an_incident()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.IntervalSeconds = 60; });
        _checker.Then(Ok, Refused, Ok);

        await host.RunCheckAsync(monitor.Id);
        host.Time.Advance(TimeSpan.FromSeconds(60));
        var failedAt = host.Now;
        var failed = await host.RunCheckAsync(monitor.Id); // returns immediately: no delay inside the run

        Assert.False(failed.IsUp);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(failedAt, m.PendingConfirmationSinceUtc);
        Assert.Equal(failedAt.AddSeconds(DelaySeconds), m.NextCheckAtUtc);
        Assert.Equal("Connection refused", m.LastError);

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        var confirmation = await host.RunCheckAsync(monitor.Id);
        Assert.True(confirmation.IsUp);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.PendingConfirmationSinceUtc);
        Assert.Null(m.LastError);
        Assert.Equal(failedAt.AddSeconds(60), m.NextCheckAtUtc); // schedule continues from the original probe
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);

        var results = await ResultsAsync(host, monitor.Id);
        Assert.Equal([true, false, true], results.Select(r => r.IsUp));
        Assert.Equal([false, false, true], results.Select(r => r.IsConfirmation));

        // The confirmation probe is not counted in uptime: 1 of 2 checks up.
        var summary = await host.InScopeAsync(sp => sp.GetRequiredService<IUptimeStatsService>()
            .GetSummariesAsync([monitor.Id], host.Now.AddHours(-1), host.Now));
        Assert.Equal(2, summary[monitor.Id].TotalChecks);
        Assert.Equal(1, summary[monitor.Id].UpChecks);
    }

    [Fact]
    public async Task Confirmed_outage_opens_incident_dated_from_first_failure_within_interval_plus_delay()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 2; m.IntervalSeconds = 30; m.Name = "Shop"; });
        _checker.Then(Ok, Refused, Refused, Timeout);

        await host.RunCheckAsync(monitor.Id);                         // Up
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var outageStart = host.Now;
        await host.RunCheckAsync(monitor.Id);                         // 1st failure: below threshold, counted normally
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(1, m.ConsecutiveFailures);
        Assert.Null(m.PendingConfirmationSinceUtc);

        host.Time.Advance(TimeSpan.FromSeconds(30));
        var failedAt = host.Now;
        await host.RunCheckAsync(monitor.Id);                         // would go DOWN: confirmation scheduled
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(failedAt, m.PendingConfirmationSinceUtc);
        Assert.Empty(_email.Sent);

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        var confirmedAt = host.Now;
        await host.RunCheckAsync(monitor.Id);                         // confirmation fails => DOWN

        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(3, m.ConsecutiveFailures);
        Assert.Equal(failedAt, m.StatusChangedAtUtc);
        Assert.Equal("Timeout after 10s", m.LastError);
        Assert.Equal(confirmedAt, m.LastCheckedAtUtc);
        Assert.Null(m.PendingConfirmationSinceUtc);
        var incident = await host.WithDbAsync(db => db.Incidents.SingleAsync());
        Assert.Equal(failedAt, incident.StartedAtUtc);
        Assert.Equal("Timeout after 10s", incident.Cause);
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(AlertKind.Down, alert.Kind);
        Assert.Equal("Timeout after 10s", alert.Error);
        Assert.Equal(failedAt, alert.OccurredAtUtc);
        Assert.Contains("Error: Timeout after 10s", alert.Body);
        Assert.True(confirmedAt - outageStart <= TimeSpan.FromSeconds(30 + DelaySeconds));

        // Already down: further failures are not confirmed again.
        host.Time.Advance(TimeSpan.FromSeconds(30));
        await host.RunCheckAsync(monitor.Id);
        Assert.Null((await host.GetMonitorAsync(monitor.Id)).PendingConfirmationSinceUtc);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Scheduler_slot_is_not_held_while_a_confirmation_is_pending()
    {
        var checker = new TargetChecker();
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = "true", ["Monitoring:ConfirmationDelaySeconds"] = "5", ["Monitoring:MaxConcurrency"] = "1" });
        var customer = await host.AddCustomerAsync();
        var bad = await host.AddMonitorAsync(customer.Id, m => { m.Target = "http://bad.test/"; m.FailureThreshold = 1; m.Status = MonitorStatus.Up; m.NextCheckAtUtc = null; });
        var good = await host.AddMonitorAsync(customer.Id, m => { m.Target = "http://good.test/"; m.IntervalSeconds = 2; m.NextCheckAtUtc = null; });
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        Assert.Equal(2, (await scheduler.DispatchDueAsync(CancellationToken.None)).Count);
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)); // no fake time passed: nobody waits for the delay
        Assert.Equal(0, scheduler.InFlightCount);
        Assert.Equal(host.Now, (await host.GetMonitorAsync(bad.Id)).PendingConfirmationSinceUtc);

        // During the delay the single slot is free for other monitors.
        host.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([good.Id], await scheduler.DispatchDueAsync(CancellationToken.None));
        await scheduler.WhenIdleAsync();

        // The confirmation is just a due run.
        host.Time.Advance(TimeSpan.FromSeconds(3));
        var dispatched = await scheduler.DispatchDueAsync(CancellationToken.None);
        Assert.Contains(bad.Id, dispatched);
        await scheduler.WhenIdleAsync();
        var m = await host.GetMonitorAsync(bad.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(MonitoringTestHost.Start.UtcDateTime, m.StatusChangedAtUtc);
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Equal(1, await host.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == bad.Id && c.IsConfirmation)));
    }

    [Fact]
    public async Task Pause_while_pending_cancels_the_confirmation()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Refused);

        await host.RunCheckAsync(monitor.Id);
        Assert.NotNull((await host.GetMonitorAsync(monitor.Id)).PendingConfirmationSinceUtc);
        await host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().PauseAsync(monitor.Id, customer.Id));

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id); // "check now" on the paused monitor: recorded only
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));

        await host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().ResumeAsync(monitor.Id, customer.Id));
        host.Time.Advance(TimeSpan.FromSeconds(1));
        var resumedAt = host.Now;
        await host.RunCheckAsync(monitor.Id); // first run after resume starts a new confirmation instead of using the old one
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.NotEqual(MonitorStatus.Down, m.Status);
        Assert.Equal(resumedAt, m.PendingConfirmationSinceUtc);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.DoesNotContain(await ResultsAsync(host, monitor.Id), r => r.IsConfirmation);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Edit_while_pending_cancels_the_confirmation_and_delete_ends_it()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Refused);

        await host.RunCheckAsync(monitor.Id);
        await host.WithDbAsync(async db =>
        {
            var m = await db.Monitors.SingleAsync(x => x.Id == monitor.Id);
            m.Target = "http://127.0.0.2/";
            m.Version++; // what the Edit page does
            await db.SaveChangesAsync();
        });

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        var secondAt = host.Now;
        await host.RunCheckAsync(monitor.Id);
        var after = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, after.Status);
        Assert.Equal(secondAt, after.PendingConfirmationSinceUtc); // evaluated from scratch for the new target

        await host.WithDbAsync(db => db.Monitors.Where(x => x.Id == monitor.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.RunCheckAsync(monitor.Id));
    }

    [Fact]
    public async Task Version_conflict_while_saving_the_confirmation_retries_on_fresh_state()
    {
        var hook = new SaveHook(db => db.ChangeTracker.Entries<CheckResult>().Any(e => e.State == EntityState.Added && e.Entity.IsConfirmation));
        await using var host = await CreateHostAsync(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(hook)));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Refused);
        hook.Action = () => host.WithDbAsync(async db =>
        {
            var m = await db.Monitors.SingleAsync(x => x.Id == monitor.Id);
            m.Name = "Renamed meanwhile";
            m.Version++;
            await db.SaveChangesAsync();
        });

        await host.RunCheckAsync(monitor.Id);
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        var confirmAt = host.Now;
        await host.RunCheckAsync(monitor.Id);

        Assert.True(hook.Fired);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal("Renamed meanwhile", m.Name);   // the user's change survives
        Assert.Equal(MonitorStatus.Up, m.Status);    // the stale confirmation decision was not applied
        Assert.Equal(confirmAt, m.PendingConfirmationSinceUtc);
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        var results = await ResultsAsync(host, monitor.Id);
        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.IsConfirmation);

        // The next run confirms normally.
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Equal(confirmAt, (await host.WithDbAsync(db => db.Incidents.SingleAsync())).StartedAtUtc);
    }

    [Fact]
    public async Task Too_old_pending_confirmation_is_evaluated_from_scratch()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; m.IntervalSeconds = 60; m.TimeoutSeconds = 10; });
        _checker.Then(Refused);

        await host.RunCheckAsync(monitor.Id);
        // e.g. the process was down: the confirmation runs much later than delay + interval + timeout + slack.
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds + 60 + 10) + MonitorStateMachine.PendingConfirmationSlack + TimeSpan.FromSeconds(1));
        var lateAt = host.Now;
        await host.RunCheckAsync(monitor.Id);

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);          // no outage backdated to the old failure
        Assert.Equal(lateAt, m.PendingConfirmationSinceUtc); // a fresh confirmation instead
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.DoesNotContain(await ResultsAsync(host, monitor.Id), r => r.IsConfirmation);

        // Just inside the limit it still confirms.
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds + 60 + 10) + MonitorStateMachine.PendingConfirmationSlack);
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Equal(lateAt, (await host.WithDbAsync(db => db.Incidents.SingleAsync())).StartedAtUtc);
    }

    [Fact]
    public async Task Confirmation_delay_starts_after_the_probe_ends()
    {
        MonitoringTestHost? hostRef = null;
        var slow = new SlowChecker(() => hostRef!.Time.Advance(TimeSpan.FromSeconds(8))); // an 8 s timeout-like probe
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(slow).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = "true", ["Monitoring:ConfirmationDelaySeconds"] = DelaySeconds.ToString() });
        hostRef = host;
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });

        var startedAt = host.Now;
        await host.RunCheckAsync(monitor.Id);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(startedAt, m.PendingConfirmationSinceUtc);
        Assert.Equal(startedAt.AddSeconds(8 + DelaySeconds), m.NextCheckAtUtc);

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.Equal(startedAt, m.StatusChangedAtUtc); // still dated from the start of the first failure
    }

    [Fact]
    public async Task Disabled_option_goes_down_without_confirmation()
    {
        await using var host = await CreateHostAsync(settings: new() { ["Monitoring:ConfirmBeforeDown"] = "false" });
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => m.FailureThreshold = 1);
        _checker.Then(Refused);

        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(1, _checker.Calls);
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
        Assert.Equal(expected, MonitorStateMachine.NeedsConfirmation(Active(status, failures, threshold), Refused));
        Assert.False(MonitorStateMachine.NeedsConfirmation(Active(status, failures, threshold), Ok));
    }

    [Fact]
    public void Pending_confirmation_is_bound_to_the_version_and_its_age()
    {
        var m = Active(MonitorStatus.Up, 0, 1);
        m.Version = 4;
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromSeconds(5);
        MonitorStateMachine.BeginConfirmation(m, Refused, t0, t0.AddSeconds(1) + delay);
        Assert.True(MonitorStateMachine.HasValidPendingConfirmation(m, t0.AddSeconds(6), delay));
        Assert.Equal(t0.AddSeconds(6), m.NextCheckAtUtc);
        Assert.Equal(0, m.ConsecutiveFailures);
        // delay 5 + interval 60 + timeout (default 10) + slack 30 = 105 s
        Assert.True(MonitorStateMachine.HasValidPendingConfirmation(m, t0.AddSeconds(105), delay));
        Assert.False(MonitorStateMachine.HasValidPendingConfirmation(m, t0.AddSeconds(106), delay));
        m.Version++;
        Assert.False(MonitorStateMachine.HasValidPendingConfirmation(m, t0.AddSeconds(6), delay));
    }

    [Fact]
    public void ApplyConfirmed_counts_both_failures_and_dates_the_outage_from_the_first()
    {
        var m = Active(MonitorStatus.Up, 1, 2);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var transition = MonitorStateMachine.ApplyConfirmed(m, t0, Timeout, t0.AddSeconds(5));
        Assert.Equal(StatusTransition.WentDown, transition);
        Assert.Equal(3, m.ConsecutiveFailures);
        Assert.Equal(t0, m.StatusChangedAtUtc);
        Assert.Equal(t0.AddSeconds(5), m.LastCheckedAtUtc);
        Assert.Equal("Timeout after 10s", m.LastError);
        Assert.Equal(t0.AddSeconds(60), m.NextCheckAtUtc);
    }

    [Fact]
    public void ApplyConfirmed_transient_resets_the_counter_and_unknown_becomes_up()
    {
        var m = Active(MonitorStatus.Unknown, 2, 3);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(StatusTransition.BecameUp, MonitorStateMachine.ApplyConfirmed(m, t0, Ok, t0.AddSeconds(5)));
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Null(m.LastError);
    }

    /// <summary>Always fails; runs <paramref name="duringProbe"/> to simulate the probe taking time.</summary>
    private sealed class SlowChecker(Action duringProbe) : IMonitorChecker
    {
        public MonitorType Type => MonitorType.Http;
        public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
        {
            duringProbe();
            return Task.FromResult(Timeout);
        }
    }

    /// <summary>Fails for targets containing "bad", succeeds otherwise.</summary>
    private sealed class TargetChecker : IMonitorChecker
    {
        public MonitorType Type => MonitorType.Http;
        public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct) =>
            Task.FromResult(monitor.Target.Contains("bad") ? Refused : Ok);
    }
}
