using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Cluster;
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
