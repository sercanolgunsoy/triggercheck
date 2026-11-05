using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Cluster;
using TriggerCheck.Tests.Ai;

namespace TriggerCheck.Tests.Ops;

/// <summary>OPS-3 for AI-5: the settings suggestion job runs on the lease holder only and stops writing when it loses the lease.</summary>
public class SettingsSuggestionLeaderTests
{
    private static Task<int> FlappingMonitorAsync(AiTestHost h, int customerId) => h.WithDbAsync(async db =>
    {
        var m = new Domain.Entities.Monitor { CustomerId = customerId, Name = "Flappy", Type = MonitorType.Http, Target = "https://flap.example.test/",
            IntervalSeconds = 60, FailureThreshold = 1, Status = MonitorStatus.Up, CreatedAtUtc = h.Now.AddDays(-60) };
        db.Monitors.Add(m);
        for (var i = 0; i < 10; i++)
        {
            var start = h.Now.AddHours(-1 - i * 5);
            db.Incidents.Add(new Incident { Monitor = m, StartedAtUtc = start, ResolvedAtUtc = start.AddSeconds(62) });
        }
        await db.SaveChangesAsync();
        return m.Id;
    });

    private static Task<bool> AnySuggestionAsync(AiTestHost h, int monitorId) =>
        h.WithDbAsync(db => db.SettingsSuggestions.AnyAsync(s => s.MonitorId == monitorId));

    [Fact]
    public async Task Suggestion_job_runs_only_on_the_lease_holder()
    {
        await using var h = await AiTestHost.CreateAsync(new() { ["Ai:Suggestions:IntervalMinutes"] = "1" });
        var c = await h.AddCustomerAsync();
        var m = await FlappingMonitorAsync(h, c.Id);
        await h.WithDbAsync(db =>
        {
            db.ClusterLeases.Add(new ClusterLease { Name = SettingsSuggestionHostedService.LeaseName, Owner = "other-instance", LeasedUntilUtc = h.Now.AddDays(1) });
            return db.SaveChangesAsync();
        });
        var service = h.Services.GetServices<IHostedService>().OfType<SettingsSuggestionHostedService>().Single();
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 5; i++) // past the start delay and a few intervals
            {
                h.Time.Advance(TimeSpan.FromMinutes(1));
                await Task.Delay(100);
            }
            Assert.False(await AnySuggestionAsync(h, m));

            await h.WithDbAsync(db => db.ClusterLeases.Where(l => l.Name == SettingsSuggestionHostedService.LeaseName)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.LeasedUntilUtc, h.Now)));
            for (var i = 0; i < 200 && !await AnySuggestionAsync(h, m); i++)
            {
                h.Time.Advance(TimeSpan.FromSeconds(20));
                await Task.Delay(25);
            }
            Assert.True(await AnySuggestionAsync(h, m));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
        // Released on stop: another instance can take over at once.
        Assert.True(await h.WithDbAsync(db => db.ClusterLeases.Where(l => l.Name == SettingsSuggestionHostedService.LeaseName)
            .Select(l => l.LeasedUntilUtc).SingleAsync()) <= h.Now);
    }

    [Fact]
    public async Task Suggestion_run_writes_nothing_when_its_lease_is_lost()
    {
        await using var h = await AiTestHost.CreateAsync();
        var c = await h.AddCustomerAsync();
        var m = await FlappingMonitorAsync(h, c.Id);
        var asked = 0;
        var summary = await h.InScopeAsync(sp => sp.GetRequiredService<SettingsSuggestionJob>()
            .RunAsync(CancellationToken.None, _ => { asked++; return Task.FromResult(false); }));
        Assert.Equal(1, asked);
        Assert.Equal((0, 0), (summary.Created, summary.Queued));
        Assert.False(await AnySuggestionAsync(h, m));
    }
}
