using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>Pause/resume semantics (ENG-1) and the check runner vs. UI race (ENG-2).</summary>
public class MonitorLifecycleTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Fail = CheckOutcome.Down("Connection refused", 3);

    private readonly RecordingNotifier _email = new(AlertChannelType.Email);

    private Task<MonitoringTestHost> CreateHostAsync(IMonitorChecker checker) =>
        MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(_email));

    private static Task<MonitorLifecycleResult?> PauseAsync(MonitoringTestHost host, int id, int? customerId = null) =>
        host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().PauseAsync(id, customerId));

    private static Task<MonitorLifecycleResult?> ResumeAsync(MonitoringTestHost host, int id, int? customerId = null) =>
        host.InScopeAsync(sp => sp.GetRequiredService<MonitorLifecycle>().ResumeAsync(id, customerId));

    private static Task<int> OpenIncidentsAsync(MonitoringTestHost host, int id) =>
        host.WithDbAsync(db => db.Incidents.CountAsync(i => i.MonitorId == id && i.ResolvedAtUtc == null));

    // ---------------- ENG-1 ----------------

    [Theory]
    [InlineData(true)]  // customer panel (tenant-scoped)
    [InlineData(false)] // SuperAdmin (any tenant)
    public async Task Uat_down_pause_resume_up_leaves_no_open_incident_and_sends_up_alert(bool tenantScoped)
    {
        var checker = new ScriptedChecker(MonitorType.Http).Then(Fail, Ok);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => m.FailureThreshold = 1);
        int? scope = tenantScoped ? c.Id : null;

        await host.RunCheckAsync(monitor.Id);                         // -> Down, incident, DOWN alert
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Equal(1, await OpenIncidentsAsync(host, monitor.Id));

        host.Time.Advance(TimeSpan.FromMinutes(5));
        var pausedAt = host.Now;
        Assert.True((await PauseAsync(host, monitor.Id, scope))!.Changed);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.False(m.IsEnabled);
        Assert.True(m.DownWhenPaused);
        Assert.Equal(0, await OpenIncidentsAsync(host, monitor.Id)); // closed at pause time
        Assert.Equal(pausedAt, (await host.WithDbAsync(db => db.Incidents.SingleAsync())).ResolvedAtUtc);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await ResumeAsync(host, monitor.Id, scope))!.Changed);
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Unknown, m.Status);
        Assert.True(m.IsEnabled);
        Assert.Equal(host.Now, m.NextCheckAtUtc); // checked right away

        await host.RunCheckAsync(monitor.Id);                         // -> Up
        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.False(m.DownWhenPaused);
        Assert.Equal(0, await OpenIncidentsAsync(host, monitor.Id));
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Equal([AlertKind.Down, AlertKind.Up], _email.Sent.Select(s => s.Message.Kind));

        // Stays up: no further alerts.
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(2, _email.Sent.Count);
    }

    [Fact]
    public async Task Resume_of_monitor_paused_while_up_does_not_send_up_alert()
    {
        var checker = new ScriptedChecker(MonitorType.Http).Then(Ok);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id);

        await host.RunCheckAsync(monitor.Id);
        await PauseAsync(host, monitor.Id);
        await ResumeAsync(host, monitor.Id);
        await host.RunCheckAsync(monitor.Id);

        Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Still_down_after_resume_opens_a_new_incident_and_alerts_down()
    {
        var checker = new ScriptedChecker(MonitorType.Http).Then(Fail);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => m.FailureThreshold = 1);

        await host.RunCheckAsync(monitor.Id);
        await PauseAsync(host, monitor.Id);
        await ResumeAsync(host, monitor.Id);
        await host.RunCheckAsync(monitor.Id);

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        Assert.False(m.DownWhenPaused);
        Assert.Equal(1, await OpenIncidentsAsync(host, monitor.Id));
        Assert.Equal(2, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Equal([AlertKind.Down, AlertKind.Down], _email.Sent.Select(s => s.Message.Kind));
    }

    [Fact]
    public async Task Resume_closes_incident_left_open_by_legacy_pause()
    {
        await using var host = await CreateHostAsync(new ScriptedChecker(MonitorType.Http).Then(Ok));
        var c = await host.AddCustomerAsync();
        // State produced by the old SuperAdmin pause: Paused with an open incident.
        var monitor = await host.AddMonitorAsync(c.Id, m => { m.Status = MonitorStatus.Paused; m.IsEnabled = false; });
        await host.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = monitor.Id, StartedAtUtc = host.Now.AddHours(-1) });
            await db.SaveChangesAsync();
        });

        await ResumeAsync(host, monitor.Id);
        Assert.Equal(0, await OpenIncidentsAsync(host, monitor.Id));
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal(AlertKind.Up, Assert.Single(_email.Sent).Message.Kind);
    }

    [Fact]
    public async Task Pause_and_resume_are_tenant_scoped_idempotent_and_bump_version()
    {
        await using var host = await CreateHostAsync(new ScriptedChecker(MonitorType.Http));
        var c = await host.AddCustomerAsync();
        var other = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id);

        Assert.Null(await PauseAsync(host, monitor.Id, other.Id));
        Assert.Null(await PauseAsync(host, 999_999));
        Assert.Equal(0, (await host.GetMonitorAsync(monitor.Id)).Version);

        Assert.True((await PauseAsync(host, monitor.Id, c.Id))!.Changed);
        Assert.False((await PauseAsync(host, monitor.Id, c.Id))!.Changed);
        Assert.Equal(1, (await host.GetMonitorAsync(monitor.Id)).Version);

        Assert.True((await ResumeAsync(host, monitor.Id, c.Id))!.Changed);
        Assert.False((await ResumeAsync(host, monitor.Id, c.Id))!.Changed);
        Assert.Equal(2, (await host.GetMonitorAsync(monitor.Id)).Version);
    }

    // ---------------- ENG-2 ----------------

    [Fact]
    public async Task Pause_during_probe_wins_no_down_transition_or_incident()
    {
        var checker = new BlockingChecker(Fail);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => { m.Status = MonitorStatus.Up; m.FailureThreshold = 1; });

        var run = host.RunCheckAsync(monitor.Id);
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await PauseAsync(host, monitor.Id);                           // user pauses while the probe is in flight
        checker.Release();
        await run;

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Paused, m.Status);
        Assert.False(m.IsEnabled);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Equal("Connection refused", m.LastError);              // probe is still recorded
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Edit_of_probe_settings_during_probe_discards_stale_result()
    {
        var checker = new BlockingChecker(Fail);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => { m.Status = MonitorStatus.Up; m.FailureThreshold = 1; });

        var run = host.RunCheckAsync(monitor.Id);
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EditAsync(host, monitor.Id, m => m.Target = "http://127.0.0.2/");
        checker.Release();
        await run;

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal("http://127.0.0.2/", m.Target);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Null(m.LastCheckedAtUtc);
        Assert.Equal(0, await host.WithDbAsync(db => db.CheckResults.CountAsync()));
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Cosmetic_edit_during_probe_is_kept_and_result_still_applied()
    {
        var checker = new BlockingChecker(Fail);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => { m.Status = MonitorStatus.Up; m.FailureThreshold = 1; });

        var run = host.RunCheckAsync(monitor.Id);
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await EditAsync(host, monitor.Id, m => m.Name = "Renamed");
        checker.Release();
        await run;

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal("Renamed", m.Name);                              // edit not overwritten
        Assert.Equal(MonitorStatus.Down, m.Status);                   // probe applied
        Assert.Equal(1, await host.WithDbAsync(db => db.Incidents.CountAsync()));
    }

    [Fact]
    public async Task Delete_during_probe_discards_result()
    {
        var checker = new BlockingChecker(Fail);
        await using var host = await CreateHostAsync(checker);
        var c = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(c.Id, m => m.FailureThreshold = 1);

        var run = host.RunCheckAsync(monitor.Id);
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.WithDbAsync(db => db.Monitors.Where(m => m.Id == monitor.Id).ExecuteDeleteAsync());
        checker.Release();
        await run;

        Assert.Equal(0, await host.WithDbAsync(db => db.CheckResults.CountAsync()));
        Assert.Equal(0, await host.WithDbAsync(db => db.Incidents.CountAsync()));
        Assert.Empty(_email.Sent);
        Assert.Equal(0, host.Services.GetRequiredService<MonitorRunGate>().Count);
    }

    /// <summary>A user edit as the Edit page does it (bumps the concurrency token).</summary>
    private static Task EditAsync(MonitoringTestHost host, int id, Action<MonitorEntity> edit) =>
        host.WithDbAsync(async db =>
        {
            var m = await db.Monitors.SingleAsync(x => x.Id == id);
            edit(m);
            m.Version++;
            await db.SaveChangesAsync();
        });

    /// <summary>Checker that signals when the probe starts and blocks until released.</summary>
    private sealed class BlockingChecker(CheckOutcome outcome) : IMonitorChecker
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MonitorType Type => MonitorType.Http;
        public void Release() => _release.TrySetResult();

        public async Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return outcome;
        }
    }
}
