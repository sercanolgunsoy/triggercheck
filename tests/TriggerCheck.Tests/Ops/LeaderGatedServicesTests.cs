using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Ai.Baseline;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Cluster;
using TriggerCheck.Tests.Ai;
using TriggerCheck.Tests.Ai.Features;
using TriggerCheck.Tests.Billing;
using TriggerCheck.Web.Billing;

namespace TriggerCheck.Tests.Ops;

/// <summary>
/// OPS-3: the jobs that must run once cluster-wide (billing maintenance, AI event watcher, AI baseline) do nothing while
/// another instance holds their <see cref="ClusterLease"/>, and run (taking the lease) once it expired.
/// </summary>
public class LeaderGatedServicesTests
{
    private static ClusterLease HeldByOther(string name, DateTime now) => new() { Name = name, Owner = "other-instance", LeasedUntilUtc = now.AddDays(1) };

    private static Task ExpireAsync(AppDbContext db, string name, DateTime now) =>
        db.ClusterLeases.Where(l => l.Name == name).ExecuteUpdateAsync(u => u.SetProperty(l => l.LeasedUntilUtc, now));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, Action advance)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            advance();
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not met in time");
    }

    /// <summary>Lets <paramref name="ticks"/> runs of the service pass (fake clock steps with real-time pauses).</summary>
    private static async Task TicksAsync(int ticks, Action advance)
    {
        for (var i = 0; i < ticks; i++)
        {
            advance();
            await Task.Delay(100);
        }
    }

    private sealed class RunningBillingFactory : BillingTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Billing:MaintenanceIntervalMinutes", "1");
        }
    }

    [Fact]
    public async Task Billing_maintenance_runs_only_on_the_lease_holder()
    {
        await using var f = new RunningBillingFactory();
        _ = f.CreateClient(); // starts the hosted services; the first billing run is due one (fake) minute later
        await f.WithDbAsync(db => { db.ClusterLeases.Add(HeldByOther(ClusterLeases.BillingMaintenance, f.Now)); return db.SaveChangesAsync(); });
        var (cid, _) = await f.AddCustomerWithAdminAsync("Pro", c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = f.Now.AddDays(2); });

        await TicksAsync(3, () => f.Time.Advance(TimeSpan.FromMinutes(1)));
        Assert.Null((await f.GetCustomerAsync(cid)).TrialReminderSentAtUtc);

        await f.WithDbAsync(db => ExpireAsync(db, ClusterLeases.BillingMaintenance, f.Now));
        await WaitUntilAsync(async () => (await f.GetCustomerAsync(cid)).TrialReminderSentAtUtc is not null, () => f.Time.Advance(TimeSpan.FromSeconds(20)));
        var me = f.Services.GetRequiredService<InstanceIdentity>().Id;
        Assert.Equal(me, await f.WithDbAsync(db => db.ClusterLeases.Where(l => l.Name == ClusterLeases.BillingMaintenance).Select(l => l.Owner).SingleAsync()));
    }

    [Fact]
    public async Task Ai_event_watcher_scans_only_on_the_lease_holder()
    {
        var queue = new CapturingQueue();
        await using var h = await AiTestHost.CreateAsync(new() { ["Ai:Events:PollSeconds"] = "1" }, configure: s =>
        {
            s.RemoveAll<IAiContextProvider>();
            s.AddScoped<IAiContextProvider, IncidentInterpretationProvider>();
            s.AddScoped<IAiContextProvider, SlowdownExplanationProvider>();
            s.RemoveAll<IAiJobQueue>();
            s.AddSingleton<IAiJobQueue>(queue);
        });
        var c = await h.AddCustomerAsync();
        var m = await h.AddMonitorAsync(c.Id);
        await h.WithDbAsync(db =>
        {
            db.ClusterLeases.Add(HeldByOther(ClusterLeases.AiEventWatcher, h.Now));
            db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = h.Now, Cause = "HTTP 503" });
            return db.SaveChangesAsync();
        });
        var watcher = h.Services.GetServices<IHostedService>().OfType<AiEventWatcher>().Single();
        await watcher.StartAsync(CancellationToken.None);
        try
        {
            await TicksAsync(5, () => h.Time.Advance(TimeSpan.FromSeconds(1)));
            Assert.Empty(queue.Jobs);

            await h.WithDbAsync(db => ExpireAsync(db, ClusterLeases.AiEventWatcher, h.Now));
            await WaitUntilAsync(() => Task.FromResult(!queue.Jobs.IsEmpty), () => h.Time.Advance(TimeSpan.FromSeconds(1)));
            Assert.Equal(AiFeatures.IncidentInterpretation, Assert.Single(queue.Jobs).Feature);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
        // Released on stop: another instance can take over at once.
        Assert.True(await h.WithDbAsync(db => db.ClusterLeases.Where(l => l.Name == ClusterLeases.AiEventWatcher).Select(l => l.LeasedUntilUtc).SingleAsync()) <= h.Now);
    }

    [Fact]
    public async Task Ai_baseline_job_runs_only_on_the_lease_holder()
    {
        await using var h = await AiTestHost.CreateAsync(new() { ["Ai:Baseline:IntervalSeconds"] = "1" });
        var c = await h.AddCustomerAsync();
        await h.AddMonitorAsync(c.Id);
        await h.WithDbAsync(db => { db.ClusterLeases.Add(HeldByOther(ClusterLeases.AiBaseline, h.Now)); return db.SaveChangesAsync(); });
        var service = h.Services.GetServices<IHostedService>().OfType<BaselineHostedService>().Single();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TicksAsync(5, () => h.Time.Advance(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, await h.WithDbAsync(db => db.MonitorBaselines.CountAsync()));

            await h.WithDbAsync(db => ExpireAsync(db, ClusterLeases.AiBaseline, h.Now));
            await WaitUntilAsync(async () => await h.WithDbAsync(db => db.MonitorBaselines.AnyAsync()), () => h.Time.Advance(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Baseline_run_stops_before_saving_signals_when_its_lease_is_lost()
    {
        await using var h = await AiTestHost.CreateAsync();
        var c = await h.AddCustomerAsync();
        await h.AddMonitorAsync(c.Id);
        var asked = 0;
        var summary = await h.InScopeAsync(sp => sp.GetRequiredService<BaselineJob>()
            .RunAsync(CancellationToken.None, _ => { asked++; return Task.FromResult(false); }));
        Assert.Equal(1, asked);
        Assert.Equal(0, summary.SignalsOpened);
    }

    [Fact]
    public async Task Billing_run_skips_the_remaining_items_when_its_lease_is_lost()
    {
        await using var f = new BillingTestFactory();
        var (cid, email) = await f.AddCustomerWithAdminAsync("Pro", c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = f.Now.AddDays(2); });
        using var scope = f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BillingMaintenance>().RunAsync(CancellationToken.None, _ => Task.FromResult(false));
        Assert.Null((await f.GetCustomerAsync(cid)).TrialReminderSentAtUtc);
        Assert.Empty(f.Emails.To(email));
    }

    [Fact]
    public async Task Watcher_lease_grant_tells_an_uninterrupted_hold_from_a_reacquisition()
    {
        await using var h = await AiTestHost.CreateAsync();
        var length = TimeSpan.FromMinutes(1);
        Task<LeaseGrant> Acquire() => h.InScopeAsync(sp => sp.GetRequiredService<ClusterLeases>().AcquireAsync("job", length, CancellationToken.None));

        Assert.Equal(LeaseGrant.Acquired, await Acquire());
        h.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(LeaseGrant.Renewed, await Acquire());
        h.Time.Advance(TimeSpan.FromMinutes(1));                       // own lease expired: someone else could have run meanwhile
        Assert.Equal(LeaseGrant.Acquired, await Acquire());
        await h.WithDbAsync(db => db.ClusterLeases.ExecuteUpdateAsync(u => u.SetProperty(l => l.Owner, "other-instance")));
        Assert.Equal(LeaseGrant.Denied, await Acquire());
    }
}
