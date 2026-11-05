using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Cluster;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Monitoring.Scheduling;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>OPS-3: two app instances (own DI container, scheduler, run gate and clock) sharing one SQLite database.</summary>
public class MultiInstanceSchedulerTests
{
    private static async Task<(MonitoringTestHost A, MonitoringTestHost B)> CreatePairAsync(IMonitorChecker checkerA, IMonitorChecker checkerB)
    {
        var a = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checkerA), new() { ["Monitoring:InstanceId"] = "instance-a" });
        var shared = a.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default");
        var b = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checkerB),
            new() { ["Monitoring:InstanceId"] = "instance-b", ["ConnectionStrings:Default"] = shared });
        return (a, b);
    }

    private static void Advance(TimeSpan step, params MonitoringTestHost[] hosts)
    {
        foreach (var h in hosts) h.Time.Advance(step);
    }

    [Fact]
    public async Task Two_instances_check_every_slot_exactly_once()
    {
        var checkerA = new ScriptedChecker(MonitorType.Http);
        var checkerB = new ScriptedChecker(MonitorType.Http);
        var (a, b) = await CreatePairAsync(checkerA, checkerB);
        await using var hostA = a;
        await using var hostB = b;
        var c = await a.AddCustomerAsync();
        var ids = new List<int>();
        for (var i = 0; i < 10; i++) ids.Add((await a.AddMonitorAsync(c.Id, m => m.IntervalSeconds = 15)).Id);
        var schedulerA = a.Services.GetRequiredService<MonitorScheduler>();
        var schedulerB = b.Services.GetRequiredService<MonitorScheduler>();

        // 30 ticks of 5 s (t = 0..145): both instances see the same due list every tick and race for each slot.
        for (var tick = 0; tick < 30; tick++)
        {
            await Task.WhenAll(schedulerA.DispatchDueAsync(CancellationToken.None), schedulerB.DispatchDueAsync(CancellationToken.None));
            await Task.WhenAll(schedulerA.WhenIdleAsync(), schedulerB.WhenIdleAsync());
            Advance(TimeSpan.FromSeconds(5), a, b);
        }

        var results = await a.WithDbAsync(db => db.CheckResults.AsNoTracking().Select(r => new { r.MonitorId, r.CheckedAtUtc }).ToListAsync());
        foreach (var id in ids)
        {
            var times = results.Where(r => r.MonitorId == id).Select(r => r.CheckedAtUtc).ToList();
            Assert.Equal(10, times.Count);                 // slots at 0, 15, ..., 135 s
            Assert.Equal(times.Count, times.Distinct().Count());
        }
        Assert.Equal(100, checkerA.Calls + checkerB.Calls); // no probe ran without being recorded
        Assert.All(await a.WithDbAsync(db => db.Monitors.AsNoTracking().ToListAsync()), m =>
        {
            Assert.Null(m.LeaseOwner);                      // every lease released after its run
            Assert.Null(m.LeasedUntilUtc);
        });
    }

    [Fact]
    public async Task Slot_claimed_by_one_instance_cannot_be_claimed_by_the_other_even_from_a_stale_due_list()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => x.NextCheckAtUtc = host.Now);
        var now = host.Now;
        var length = MonitorLease.Length(10, 30);

        Assert.True(await host.WithDbAsync(db => MonitorLease.TryClaimAsync(db, m.Id, "a", now, length, CancellationToken.None)));
        Assert.False(await host.WithDbAsync(db => MonitorLease.TryClaimAsync(db, m.Id, "b", now, length, CancellationToken.None)));
        Assert.Empty(await host.WithDbAsync(db => DueMonitorQuery.SelectUnleasedDueAsync(db, now, 10, CancellationToken.None)));

        // "a" checks the slot (next check moves on) and releases: "b", still holding the old due list, must not run it again.
        await host.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.NextCheckAtUtc, now.AddSeconds(60))));
        Assert.False(await host.WithDbAsync(db => MonitorLease.ReleaseAsync(db, m.Id, "b", CancellationToken.None))); // not b's lease
        Assert.True(await host.WithDbAsync(db => MonitorLease.ReleaseAsync(db, m.Id, "a", CancellationToken.None)));
        Assert.False(await host.WithDbAsync(db => MonitorLease.TryClaimAsync(db, m.Id, "b", now, length, CancellationToken.None)));
        Assert.True(await host.WithDbAsync(db => MonitorLease.TryClaimAsync(db, m.Id, "b", now.AddSeconds(60), length, CancellationToken.None)));
    }

    [Fact]
    public async Task Lease_of_a_crashed_instance_is_reclaimed_after_it_expires()
    {
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker));
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x =>
        {
            x.NextCheckAtUtc = host.Now;
            x.LeaseOwner = "crashed-instance";
            x.LeasedUntilUtc = host.Now.AddSeconds(50);
        });
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        Assert.Empty(await scheduler.DispatchDueAsync(CancellationToken.None)); // still leased
        host.Time.Advance(TimeSpan.FromSeconds(49));
        Assert.Empty(await scheduler.DispatchDueAsync(CancellationToken.None));
        Assert.Equal(0, checker.Calls);

        host.Time.Advance(TimeSpan.FromSeconds(1));                            // expired
        Assert.Equal(new[] { m.Id }, await scheduler.DispatchDueAsync(CancellationToken.None));
        await scheduler.WhenIdleAsync();

        Assert.Equal(1, checker.Calls);
        var after = await host.GetMonitorAsync(m.Id);
        Assert.Null(after.LeaseOwner);
        Assert.True(after.NextCheckAtUtc > host.Now);
    }

    [Fact]
    public async Task Lease_is_released_when_the_check_result_is_discarded()
    {
        var checker = new EditingChecker();
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker));
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);
        // The probe settings are edited during the probe: the runner discards the result (MonitorCheckDiscardedException).
        checker.OnCheck = () => host.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Target, "http://edited.example/")));
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        await scheduler.DispatchDueAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();

        var after = await host.GetMonitorAsync(m.Id);
        Assert.Null(after.LeaseOwner);                                          // result discarded (edited), lease still released
        Assert.Equal(new[] { m.Id }, await scheduler.DispatchDueAsync(CancellationToken.None)); // and the monitor is due again at once
        await scheduler.WhenIdleAsync();
    }

    [Fact]
    public void Lease_length_covers_a_gate_wait_plus_the_probe_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(2 * 10 + 30), MonitorLease.Length(10, 30));
        Assert.Equal(TimeSpan.FromSeconds(2 * 10 + 30), MonitorLease.Length(0, 30));   // unset timeout = 10 s like the probes
        Assert.Equal(TimeSpan.FromSeconds(2 * 120 + 5), MonitorLease.Length(999, 0)); // clamped like the probes; margin >= 5 s
    }

    [Fact]
    public async Task Cluster_lease_sticks_to_its_holder_and_moves_on_expiry_or_release()
    {
        var (a, b) = await CreatePairAsync(new ScriptedChecker(MonitorType.Http), new ScriptedChecker(MonitorType.Http));
        await using var hostA = a;
        await using var hostB = b;
        var length = TimeSpan.FromMinutes(1);
        Task<bool> Acquire(MonitoringTestHost h) => h.InScopeAsync(sp => sp.GetRequiredService<ClusterLeases>().TryAcquireAsync("job", length, CancellationToken.None));

        var first = await Task.WhenAll(Acquire(a), Acquire(b));       // concurrent first acquisition: exactly one wins
        Assert.Single(first, true);
        var (holder, other) = first[0] ? (a, b) : (b, a);

        Advance(TimeSpan.FromSeconds(50), a, b);
        Assert.True(await Acquire(holder));                            // renewal
        Assert.False(await Acquire(other));
        Advance(TimeSpan.FromSeconds(59), a, b);
        Assert.False(await Acquire(other));                            // renewed lease not expired yet
        Advance(TimeSpan.FromSeconds(1), a, b);
        Assert.True(await Acquire(other));                             // holder stopped renewing: taken over
        Assert.False(await Acquire(holder));

        await ClusterLeases.ReleaseOnStopAsync(other.Services.GetRequiredService<IServiceScopeFactory>(), "job");
        Assert.True(await Acquire(holder));                            // released on stop: no wait for expiry
    }

    [Fact]
    public void Instance_id_defaults_to_a_unique_value_and_validates_configured_ones()
    {
        var x = InstanceIdentity.Resolve(null);
        Assert.StartsWith(Environment.MachineName, x);
        Assert.NotEqual(x, InstanceIdentity.Resolve(" "));
        Assert.InRange(x.Length, 1, InstanceIdentity.MaxLength);
        Assert.Equal("web-1", InstanceIdentity.Resolve(" web-1 "));
        Assert.Throws<InvalidOperationException>(() => InstanceIdentity.Resolve(new string('x', InstanceIdentity.MaxLength + 1)));
    }

    /// <summary>Advances <paramref name="host"/>'s clock in poll steps until <paramref name="task"/> completes (bounded).</summary>
    private static async Task<T> PollUntilDoneAsync<T>(MonitoringTestHost host, Task<T> task, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            host.Time.Advance(ManualCheckRunner.PollInterval);
            await Task.Delay(10);
        }
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Check_now_waits_for_a_scheduled_check_running_on_another_instance()
    {
        var checkerA = new ScriptedChecker(MonitorType.Http);
        var checkerB = new GatedChecker();
        var (a, b) = await CreatePairAsync(checkerA, checkerB);
        await using var hostA = a;
        await using var hostB = b;
        var c = await a.AddCustomerAsync();
        var m = await a.AddMonitorAsync(c.Id);
        var schedulerB = b.Services.GetRequiredService<MonitorScheduler>();

        await schedulerB.DispatchDueAsync(CancellationToken.None);
        await checkerB.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); // B's scheduled probe is running and holds the lease

        var manual = a.InScopeAsync(sp => sp.GetRequiredService<ManualCheckRunner>().RunAsync(m.Id, CancellationToken.None));
        for (var i = 0; i < 8; i++) { a.Time.Advance(ManualCheckRunner.PollInterval); await Task.Delay(20); }
        Assert.Equal(0, checkerA.Calls);                    // no overlapping probe on A
        Assert.False(manual.IsCompleted);

        checkerB.Release();
        await schedulerB.WhenIdleAsync();
        Assert.NotNull(await PollUntilDoneAsync(a, manual)); // runs once B's check is done
        Assert.Equal(1, checkerA.Calls);
        Assert.Equal(2, await a.WithDbAsync(db => db.CheckResults.CountAsync(r => r.MonitorId == m.Id)));
        Assert.Null((await a.GetMonitorAsync(m.Id)).LeaseOwner);
    }

    [Fact]
    public async Task Check_now_gives_up_while_another_instance_keeps_the_monitor_leased()
    {
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker), new() { ["Monitoring:InstanceId"] = "instance-a" });
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => x.TimeoutSeconds = 10);
        Assert.True(await host.WithDbAsync(db => MonitorLease.TryClaimAsync(db, m.Id, "instance-b", host.Now, TimeSpan.FromMinutes(5), CancellationToken.None)));

        var started = host.Now;
        var manual = host.InScopeAsync(sp => sp.GetRequiredService<ManualCheckRunner>().RunAsync(m.Id, CancellationToken.None));
        Assert.Null(await PollUntilDoneAsync(host, manual));
        Assert.Equal(0, checker.Calls);
        Assert.InRange(host.Now - started, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(16)); // timeout + 5 s
        Assert.Equal("instance-b", (await host.GetMonitorAsync(m.Id)).LeaseOwner);    // not taken over
    }

    [Fact]
    public async Task Check_now_holds_the_lease_so_no_scheduled_check_overlaps_and_the_schedule_is_unchanged()
    {
        var checker = new GatedChecker();
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker));
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => { x.IntervalSeconds = 60; x.NextCheckAtUtc = host.Now.AddSeconds(30); });
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();

        var manual = host.InScopeAsync(sp => sp.GetRequiredService<ManualCheckRunner>().RunAsync(m.Id, CancellationToken.None));
        await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Time.Advance(TimeSpan.FromSeconds(30));                        // the scheduled slot is due meanwhile
        Assert.Empty(await scheduler.DispatchDueAsync(CancellationToken.None)); // leased by the manual check
        checker.Release();
        Assert.NotNull(await manual);

        var after = await host.GetMonitorAsync(m.Id);
        Assert.Null(after.LeaseOwner);
        Assert.Equal(MonitoringTestHost.Start.UtcDateTime.AddSeconds(60), after.NextCheckAtUtc); // runner rule, as without a lease
    }

    [Fact]
    public async Task Restart_with_a_configured_instance_id_frees_its_own_leases_only()
    {
        await using var host = await MonitoringTestHost.CreateAsync(settings: new() { ["Monitoring:InstanceId"] = "web-1" });
        var c = await host.AddCustomerAsync();
        var until = host.Now.AddMinutes(5);
        var mine = await host.AddMonitorAsync(c.Id, x => { x.LeaseOwner = "web-1"; x.LeasedUntilUtc = until; });
        var theirs = await host.AddMonitorAsync(c.Id, x => { x.LeaseOwner = "web-2"; x.LeasedUntilUtc = until; });
        await host.WithDbAsync(db =>
        {
            db.ClusterLeases.Add(new ClusterLease { Name = "job-1", Owner = "web-1", LeasedUntilUtc = until });
            db.ClusterLeases.Add(new ClusterLease { Name = "job-2", Owner = "web-2", LeasedUntilUtc = until });
            return db.SaveChangesAsync();
        });

        var scheduler = host.Services.GetRequiredService<MonitorScheduler>(); // Monitoring:Enabled=false: releases, then returns
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null((await host.GetMonitorAsync(mine.Id)).LeaseOwner);
        Assert.Equal("web-2", (await host.GetMonitorAsync(theirs.Id)).LeaseOwner);
        var jobs = await host.WithDbAsync(db => db.ClusterLeases.AsNoTracking().ToDictionaryAsync(l => l.Name));
        Assert.True(jobs["job-1"].LeasedUntilUtc <= host.Now);
        Assert.Equal(until, jobs["job-2"].LeasedUntilUtc);
    }

    [Fact]
    public async Task Region_lease_is_not_handed_out_again_from_a_stale_due_list()
    {
        DateTime? movedTo = null;
        MonitoringTestHost? h = null;
        var hook = new BeforeUpdateHook("UPDATE \"MonitorRegionStates\"", async () =>
        {
            // Between the due query and the claim, a concurrent lease call leased the probe and its result moved the
            // region's next probe on (lease already cleared again).
            movedTo = h!.Now.AddSeconds(60);
            await h.WithDbAsync(db => db.MonitorRegionStates.ExecuteUpdateAsync(u => u.SetProperty(s => s.NextCheckAtUtc, movedTo)));
        });
        await using var host = h = await MonitoringTestHost.CreateAsync(s => s.ConfigureDbContext<TriggerCheck.Infrastructure.Data.AppDbContext>(o => o.AddInterceptors(hook)));
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);
        var region = await host.WithDbAsync(async db =>
        {
            await TriggerCheck.Tests.Regions.RegionTestData.SetPlanRegionsAsync(db, c.Id, 0);
            var r = await TriggerCheck.Tests.Regions.RegionTestData.AddRegionAsync(db, "eu", host.Now);
            await TriggerCheck.Tests.Regions.RegionTestData.AssignAsync(db, m.Id, r);
            return r;
        });

        var lease = await host.InScopeAsync(sp => sp.GetRequiredService<ProbeLeaseService>().LeaseAsync(region.Id, 10, CancellationToken.None));

        Assert.True(hook.Fired);
        Assert.Empty(lease.Items);
        var state = await host.WithDbAsync(db => db.MonitorRegionStates.AsNoTracking().SingleAsync());
        Assert.Null(state.LeaseItemId);
        Assert.Equal(movedTo, state.NextCheckAtUtc);
    }

    /// <summary>Runs <see cref="Action"/> once, right before the first non-query command whose text starts with a prefix.</summary>
    private sealed class BeforeUpdateHook(string prefix, Func<Task> action) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired == 1;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith(prefix, StringComparison.Ordinal) && Interlocked.Exchange(ref _fired, 1) == 0)
                await action();
            return result;
        }
    }

    /// <summary>Checker whose probe blocks until <see cref="Release"/>.</summary>
    private sealed class GatedChecker : IMonitorChecker
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MonitorType Type => MonitorType.Http;
        public void Release() => _gate.TrySetResult();

        public async Task<CheckOutcome> CheckAsync(Domain.Entities.Monitor monitor, CancellationToken ct)
        {
            Started.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return CheckOutcome.Up(5, 200);
        }
    }

    private sealed class EditingChecker : IMonitorChecker
    {
        public Func<Task>? OnCheck { get; set; }
        public MonitorType Type => MonitorType.Http;

        public async Task<CheckOutcome> CheckAsync(Domain.Entities.Monitor monitor, CancellationToken ct)
        {
            if (OnCheck is not null) await OnCheck();
            return CheckOutcome.Up(5, 200);
        }
    }
}
