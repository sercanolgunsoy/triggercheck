using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Maintenance;

/// <summary>FEAT-3: the check runner during and after maintenance windows (confirm-before-DOWN on, as in production).</summary>
public class MaintenanceEngineTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Refused = CheckOutcome.Down("Connection refused", 3);
    private const int DelaySeconds = 5;

    private readonly ScriptedChecker _checker = new(MonitorType.Http);
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);

    private Task<MonitoringTestHost> CreateHostAsync() =>
        MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(_checker).ReplaceNotifiers(_email), new()
        {
            ["Monitoring:ConfirmBeforeDown"] = "true",
            ["Monitoring:ConfirmationDelaySeconds"] = DelaySeconds.ToString()
        });

    private static Task<MaintenanceWindow> AddWindowAsync(MonitoringTestHost host, int customerId, DateTime start, DateTime end, params int[] monitorIds) =>
        host.WithDbAsync(async db =>
        {
            var w = new MaintenanceWindow { CustomerId = customerId, Name = "Upgrade", StartsAtUtc = start, EndsAtUtc = end, AppliesToAllMonitors = monitorIds.Length == 0 };
            foreach (var id in monitorIds) w.Monitors.Add(await db.Monitors.SingleAsync(m => m.Id == id));
            db.MaintenanceWindows.Add(w);
            await db.SaveChangesAsync();
            return w;
        });

    private static Task<List<Incident>> IncidentsAsync(MonitoringTestHost host, int monitorId) =>
        host.WithDbAsync(db => db.Incidents.AsNoTracking().Where(i => i.MonitorId == monitorId).OrderBy(i => i.Id).ToListAsync());

    private static Task<List<CheckResult>> ResultsAsync(MonitoringTestHost host, int monitorId) =>
        host.WithDbAsync(db => db.CheckResults.AsNoTracking().Where(c => c.MonitorId == monitorId).OrderBy(c => c.Id).ToListAsync());

    private static async Task RunEveryAsync(MonitoringTestHost host, int monitorId, int times, TimeSpan step)
    {
        for (var i = 0; i < times; i++)
        {
            await host.RunCheckAsync(monitorId);
            host.Time.Advance(step);
        }
    }

    [Fact]
    public async Task Failures_during_maintenance_are_recorded_but_open_no_incident_and_send_no_alert()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        await AddWindowAsync(host, customer.Id, host.Now.AddMinutes(-1), host.Now.AddMinutes(30));
        _checker.Then(Refused);

        await RunEveryAsync(host, monitor.Id, 5, TimeSpan.FromSeconds(DelaySeconds)); // would normally confirm and go DOWN

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.PendingConfirmationSinceUtc);
        Assert.Equal("Connection refused", m.LastError);
        Assert.Empty(await IncidentsAsync(host, monitor.Id));
        Assert.Empty(_email.Sent);
        var results = await ResultsAsync(host, monitor.Id);
        Assert.Equal(5, results.Count);
        Assert.All(results, r => { Assert.True(r.InMaintenance); Assert.False(r.IsUp); });
    }

    [Fact]
    public async Task Still_failing_after_the_window_goes_through_the_normal_flow_without_backdating()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        await host.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = customer.Id, Name = "Ops", Type = AlertChannelType.Email, Target = "ops@example.test" });
            await db.SaveChangesAsync();
        });
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; m.IntervalSeconds = 60; });
        var end = host.Now.AddMinutes(2);
        await AddWindowAsync(host, customer.Id, host.Now, end);
        _checker.Then(Refused);

        await RunEveryAsync(host, monitor.Id, 2, TimeSpan.FromSeconds(60)); // in the window
        Assert.Equal(end, host.Now);
        var firstAfter = host.Now;
        await host.RunCheckAsync(monitor.Id);                                 // first failure after the window: confirmation pending
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Equal(firstAfter, m.PendingConfirmationSinceUtc);
        Assert.Empty(_email.Sent);

        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id);                                 // confirmed

        m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Down, m.Status);
        var incident = Assert.Single(await IncidentsAsync(host, monitor.Id));
        Assert.Equal(firstAfter, incident.StartedAtUtc);                      // not backdated into the window
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(AlertKind.Down, alert.Kind);
        Assert.Equal(incident.Id, alert.IncidentId);
        Assert.Equal([true, true, false, false], (await ResultsAsync(host, monitor.Id)).Select(r => r.InMaintenance));
    }

    [Fact]
    public async Task Pending_confirmation_from_before_the_window_is_dropped()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Refused);
        await host.RunCheckAsync(monitor.Id); // confirmation pending
        Assert.NotNull((await host.GetMonitorAsync(monitor.Id)).PendingConfirmationSinceUtc);

        await AddWindowAsync(host, customer.Id, host.Now.AddSeconds(1), host.Now.AddMinutes(10));
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id); // the confirmation probe runs in the window

        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.Null(m.PendingConfirmationSinceUtc);
        Assert.Empty(await IncidentsAsync(host, monitor.Id));
        Assert.False((await ResultsAsync(host, monitor.Id)).Last().IsConfirmation);
    }

    [Fact]
    public async Task Monitor_down_before_the_window_keeps_its_incident_and_its_recovery_is_announced_as_during_maintenance()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        _checker.Then(Refused, Refused);
        await host.RunCheckAsync(monitor.Id);
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id); // DOWN, incident open, DOWN alert
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.Single(_email.Sent);

        await AddWindowAsync(host, customer.Id, host.Now.AddSeconds(30), host.Now.AddHours(1));
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.RunCheckAsync(monitor.Id); // still failing in the window
        var open = Assert.Single(await IncidentsAsync(host, monitor.Id));
        Assert.Null(open.ResolvedAtUtc);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);

        _checker.Then(Ok);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        var recoveredAt = host.Now;
        await host.RunCheckAsync(monitor.Id); // recovers in the window: the DOWN went out, so the UP does too (marked)

        Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(monitor.Id)).Status);
        var closed = Assert.Single(await IncidentsAsync(host, monitor.Id));
        Assert.Equal(recoveredAt, closed.ResolvedAtUtc);
        Assert.True(closed.ResolvedDuringMaintenance);
        Assert.Equal([AlertKind.Down, AlertKind.Up], _email.Sent.Select(s => s.Message.Kind));
        var up = _email.Sent.Last().Message;
        Assert.True(up.DuringMaintenance);
        Assert.Contains("recovered during maintenance", up.Headline);
        Assert.Equal(recoveredAt - open.StartedAtUtc, up.Downtime);
    }

    [Fact]
    public async Task Window_for_selected_monitors_does_not_cover_the_others()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var covered = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        var other = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        await AddWindowAsync(host, customer.Id, host.Now, host.Now.AddHours(1), covered.Id);
        _checker.Then(Refused);

        foreach (var id in new[] { covered.Id, other.Id }) await host.RunCheckAsync(id);
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        foreach (var id in new[] { covered.Id, other.Id }) await host.RunCheckAsync(id);

        Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(covered.Id)).Status);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(other.Id)).Status);
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(other.Id, alert.MonitorId);
    }

    [Fact]
    public async Task Another_customers_window_never_applies()
    {
        await using var host = await CreateHostAsync();
        var mine = await host.AddCustomerAsync();
        var theirs = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(mine.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        await AddWindowAsync(host, theirs.Id, host.Now, host.Now.AddHours(1));               // all monitors of the other tenant
        await AddWindowAsync(host, theirs.Id, host.Now, host.Now.AddHours(1), monitor.Id);   // forged: lists my monitor
        _checker.Then(Refused);

        await host.RunCheckAsync(monitor.Id);
        host.Time.Advance(TimeSpan.FromSeconds(DelaySeconds));
        await host.RunCheckAsync(monitor.Id);

        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(monitor.Id)).Status);
        Assert.All(await ResultsAsync(host, monitor.Id), r => Assert.False(r.InMaintenance));
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Certificate_warning_is_held_back_until_the_window_ends()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m =>
        {
            m.Status = MonitorStatus.Up; m.Target = "https://shop.example.test/"; m.CertWarnDays = 14;
            m.CertExpiresAtUtc = MonitoringTestHost.Start.UtcDateTime.AddDays(5);
        });
        await AddWindowAsync(host, customer.Id, host.Now, host.Now.AddMinutes(5));
        _checker.Then(Ok);

        await host.RunCheckAsync(monitor.Id);
        Assert.Empty(_email.Sent);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.RunCheckAsync(monitor.Id);
        Assert.Equal([AlertKind.CertificateExpiring], _email.Sent.Select(s => s.Message.Kind));
    }

    [Fact]
    public async Task Uptime_and_chart_ignore_checks_taken_in_maintenance()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.Status = MonitorStatus.Up; m.IntervalSeconds = 60; });
        _checker.Then(Ok, Ok);
        await RunEveryAsync(host, monitor.Id, 2, TimeSpan.FromMinutes(1));       // 2 up checks
        await AddWindowAsync(host, customer.Id, host.Now, host.Now.AddMinutes(3));
        _checker.Then(Refused, Refused, Refused);
        await RunEveryAsync(host, monitor.Id, 3, TimeSpan.FromMinutes(1));       // 3 failures in the window

        var from = MonitoringTestHost.Start.UtcDateTime.AddMinutes(-1);
        var summary = await host.InScopeAsync(sp => sp.GetRequiredService<IUptimeStatsService>().GetSummariesAsync([monitor.Id], from, host.Now));
        Assert.Equal(2, summary[monitor.Id].TotalChecks);
        Assert.Equal(100, summary[monitor.Id].UptimePercent);

        var series = await host.InScopeAsync(sp => sp.GetRequiredService<IResponseTimeSeriesService>().GetAsync(monitor.Id, customer.Id, from, host.Now));
        Assert.Equal(2, series.Buckets.Sum(b => b.Checks));
        Assert.Equal(0, series.Buckets.Sum(b => b.Failures));
        Assert.Equal(5, (await ResultsAsync(host, monitor.Id)).Count); // still recorded
    }

    [Fact]
    public async Task Weekly_window_is_evaluated_in_its_time_zone()
    {
        await using var host = await CreateHostAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; });
        // Start is Thursday 2026-01-01 12:00 UTC = 13:00 in Berlin: a Thursday 12:30-14:30 Berlin window covers it.
        await host.WithDbAsync(async db =>
        {
            db.MaintenanceWindows.Add(new MaintenanceWindow
            {
                CustomerId = customer.Id, Name = "Weekly", IsRecurring = true, TimeZoneId = "Europe/Berlin",
                RecurrenceDays = WeekDays.Thursday, RecurrenceStartMinutes = 12 * 60 + 30, DurationMinutes = 120
            });
            await db.SaveChangesAsync();
        });
        _checker.Then(Refused);
        await host.RunCheckAsync(monitor.Id);
        Assert.True((await ResultsAsync(host, monitor.Id)).Single().InMaintenance);

        host.Time.Advance(TimeSpan.FromMinutes(90)); // 14:30 Berlin: window over
        await host.RunCheckAsync(monitor.Id);
        Assert.False((await ResultsAsync(host, monitor.Id)).Last().InMaintenance);
        Assert.NotNull((await host.GetMonitorAsync(monitor.Id)).PendingConfirmationSinceUtc);
    }
}
