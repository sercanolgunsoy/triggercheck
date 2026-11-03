using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Scheduling;

namespace TriggerCheck.Tests.Monitoring;

public class MonitorSchedulerTests
{
    [Fact]
    public async Task Due_selection_excludes_paused_disabled_future_and_suspended_customers()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var active = await host.AddCustomerAsync();
        var suspended = await host.AddCustomerAsync(isActive: false);
        var now = host.Now;

        var neverChecked = await host.AddMonitorAsync(active.Id, m => m.NextCheckAtUtc = null);
        var overdue = await host.AddMonitorAsync(active.Id, m => { m.NextCheckAtUtc = now.AddSeconds(-5); m.Status = MonitorStatus.Down; });
        var exactlyNow = await host.AddMonitorAsync(active.Id, m => m.NextCheckAtUtc = now);
        await host.AddMonitorAsync(active.Id, m => m.NextCheckAtUtc = now.AddSeconds(1));                          // future
        await host.AddMonitorAsync(active.Id, m => { m.NextCheckAtUtc = null; m.Status = MonitorStatus.Paused; }); // paused
        await host.AddMonitorAsync(active.Id, m => { m.NextCheckAtUtc = null; m.IsEnabled = false; });            // disabled
        await host.AddMonitorAsync(suspended.Id, m => m.NextCheckAtUtc = null);                                    // suspended customer

        var due = await host.WithDbAsync(db => DueMonitorQuery.SelectDueIdsAsync(db, now, 100, CancellationToken.None));

        Assert.Equal(new[] { neverChecked.Id, overdue.Id, exactlyNow.Id }.Order(), due.Order());
        Assert.Equal(neverChecked.Id, due[0]); // never-checked monitors first
    }

    [Fact]
    public async Task In_flight_monitor_is_not_dispatched_twice()
    {
        var runner = new BlockingRunner();
        await using var host = await MonitoringTestHost.CreateAsync(s =>
        {
            s.RemoveAll<IMonitorCheckRunner>();
            s.AddSingleton<IMonitorCheckRunner>(runner);
        });
        var c = await host.AddCustomerAsync();
        var a = await host.AddMonitorAsync(c.Id);
        var b = await host.AddMonitorAsync(c.Id);
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        var first = await scheduler.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(2, first.Count);
        await runner.WaitForCallsAsync(2);

        // runner hasn't finished (NextCheckAtUtc not advanced) -> monitors still due but in flight
        var second = await scheduler.DispatchDueAsync(CancellationToken.None);
        Assert.Empty(second);
        Assert.Equal(2, scheduler.InFlightCount);

        runner.Release();
        await scheduler.WhenIdleAsync();
        Assert.Equal(0, scheduler.InFlightCount);

        var third = await scheduler.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), third.Order());
        await scheduler.WhenIdleAsync();
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Concurrency_is_bounded_by_MaxConcurrency()
    {
        var runner = new BlockingRunner();
        await using var host = await MonitoringTestHost.CreateAsync(
            s => { s.RemoveAll<IMonitorCheckRunner>(); s.AddSingleton<IMonitorCheckRunner>(runner); },
            new() { ["Monitoring:MaxConcurrency"] = "2" });
        var c = await host.AddCustomerAsync();
        for (var i = 0; i < 5; i++) await host.AddMonitorAsync(c.Id);
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        Assert.Equal(5, (await scheduler.DispatchDueAsync(CancellationToken.None)).Count);
        await runner.WaitForCallsAsync(2);
        await Task.Delay(100);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(5, scheduler.InFlightCount);

        runner.Release();
        await scheduler.WhenIdleAsync();
        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task Failing_check_does_not_stop_other_checks()
    {
        var runner = new BlockingRunner(failFor: id => id % 2 == 1);
        runner.Release();
        await using var host = await MonitoringTestHost.CreateAsync(s => { s.RemoveAll<IMonitorCheckRunner>(); s.AddSingleton<IMonitorCheckRunner>(runner); });
        var c = await host.AddCustomerAsync();
        for (var i = 0; i < 4; i++) await host.AddMonitorAsync(c.Id);
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        await scheduler.DispatchDueAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(4, runner.Calls.Count);
        Assert.Equal(0, scheduler.InFlightCount);
    }

    [Fact]
    public async Task Scheduler_loop_checks_due_monitors_end_to_end_and_respects_interval()
    {
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(
            s => s.ReplaceCheckers(checker),
            new() { ["Monitoring:Enabled"] = "true", ["Monitoring:SchedulerTickMilliseconds"] = "1000" });
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => x.IntervalSeconds = 15);
        var scheduler = host.Services.GetServices<IHostedService>().OfType<MonitorScheduler>().Single();

        using var cts = new CancellationTokenSource();
        await scheduler.StartAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => checker.Calls == 1);       // first tick runs immediately
            await scheduler.WhenIdleAsync();
            Assert.Equal(MonitorStatus.Up, (await host.GetMonitorAsync(m.Id)).Status);

            for (var s = 0; s < 14; s++) { host.Time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(20); }
            await scheduler.WhenIdleAsync();
            Assert.Equal(1, checker.Calls);                       // not yet due at t+14s

            host.Time.Advance(TimeSpan.FromSeconds(1));           // t+15s
            await WaitUntilAsync(() => checker.Calls == 2);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Hosted_services_do_nothing_when_disabled()
    {
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker)); // Enabled=false
        var c = await host.AddCustomerAsync();
        await host.AddMonitorAsync(c.Id);

        foreach (var hs in host.Services.GetServices<IHostedService>()) await hs.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        foreach (var hs in host.Services.GetServices<IHostedService>()) await hs.StopAsync(CancellationToken.None);

        Assert.Equal(0, checker.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    /// <summary>Runner that records calls and blocks until <see cref="Release"/>.</summary>
    private sealed class BlockingRunner(Func<int, bool>? failFor = null) : IMonitorCheckRunner
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<int> Calls { get; } = new();

        public void Release() => _gate.TrySetResult();

        public async Task WaitForCallsAsync(int count) => await WaitUntilAsync(() => Calls.Count >= count);

        public async Task<CheckResult> RunAsync(int monitorId, CancellationToken ct = default)
        {
            Calls.Enqueue(monitorId);
            await _gate.Task.WaitAsync(ct);
            if (failFor?.Invoke(monitorId) == true) throw new InvalidOperationException("db down");
            return new CheckResult { MonitorId = monitorId, IsUp = true };
        }
    }
}
