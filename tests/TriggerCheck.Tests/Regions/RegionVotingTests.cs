using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Regions;

/// <summary>FEAT-6: majority voting between the central scheduler and remote regions (engine level, fake clock).</summary>
public class RegionVotingTests
{
    private static readonly CheckOutcome Ok = CheckOutcome.Up(30, 200);
    private static readonly CheckOutcome Refused = CheckOutcome.Down("Connection refused", 3);

    private readonly ScriptedChecker _central = new(MonitorType.Http);
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);

    private Task<MonitoringTestHost> CreateHostAsync(bool confirmBeforeDown = false) =>
        MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(_central).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = confirmBeforeDown ? "true" : "false" });

    private sealed record Setup(MonitoringTestHost Host, int MonitorId, TestRegion A, TestRegion B);

    /// <summary>Monitor (threshold 1, 60 s) checked from central + regions a and b; plan allows all regions.</summary>
    private async Task<Setup> ArrangeAsync(bool confirmBeforeDown = false)
    {
        var host = await CreateHostAsync(confirmBeforeDown);
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.IntervalSeconds = 60; });
        var (a, b) = await host.WithDbAsync(async db =>
        {
            await RegionTestData.SetPlanRegionsAsync(db, customer.Id, 0);
            var ra = await RegionTestData.AddRegionAsync(db, "a", host.Now);
            var rb = await RegionTestData.AddRegionAsync(db, "b", host.Now);
            await RegionTestData.AssignAsync(db, monitor.Id, ra, rb);
            return (ra, rb);
        });
        return new Setup(host, monitor.Id, a, b);
    }

    private static Task<LeaseResponse> LeaseAsync(MonitoringTestHost host, TestRegion region) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ProbeLeaseService>().LeaseAsync(region.Id, 10, CancellationToken.None));

    /// <summary>Leases the region's single due probe and reports <paramref name="up"/> for it.</summary>
    private static async Task<ResultStatus> ReportAsync(MonitoringTestHost host, TestRegion region, bool up, string error = "Connection refused")
    {
        await host.WithDbAsync(db => RegionTestData.TouchAsync(db, region, host.Now));
        await host.WithDbAsync(db => db.MonitorRegionStates.Where(s => s.RegionId == region.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextCheckAtUtc, (DateTime?)null)));
        var lease = await LeaseAsync(host, region);
        var item = Assert.Single(lease.Items);
        var result = up ? RegionTestData.Up(item.ItemId, host.Now) : RegionTestData.Down(item.ItemId, host.Now, error);
        var acks = await host.InScopeAsync(sp => sp.GetRequiredService<ProbeLeaseService>().IngestAsync(region.Id, [result], CancellationToken.None));
        await host.AlertsIdleAsync();
        return Assert.Single(acks).Status;
    }

    private static Task<MonitorStatus> StatusAsync(MonitoringTestHost host, int id) =>
        host.WithDbAsync(db => db.Monitors.Where(m => m.Id == id).Select(m => m.Status).SingleAsync());

    private static Task<List<Incident>> IncidentsAsync(MonitoringTestHost host, int id) =>
        host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == id).ToListAsync());

    [Fact]
    public async Task Two_of_three_regions_failing_take_the_monitor_down()
    {
        var (host, id, a, b) = await ArrangeAsync();
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));

        host.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, a, up: false));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id)); // 1 of 3
        Assert.Empty(await IncidentsAsync(host, id));
        // The first failure asks central for a quick confirmation.
        var m = await host.GetMonitorAsync(id);
        Assert.True(m.NextCheckAtUtc <= host.Now.AddSeconds(5));

        host.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, b, up: false, error: "Timeout after 10s"));
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id)); // 2 of 3

        var incident = Assert.Single(await IncidentsAsync(host, id));
        Assert.Equal("a,b", incident.FailedRegions);
        Assert.Contains("a: Connection refused", incident.Cause);
        Assert.Contains("b: Timeout after 10s", incident.Cause);
        Assert.Equal(MonitoringTestHost.Start.UtcDateTime.AddSeconds(2), incident.StartedAtUtc); // dated from the first failing vote
        var (_, alert) = Assert.Single(_email.Sent);
        Assert.Equal(TriggerCheck.Monitoring.Alerting.AlertKind.Down, alert.Kind);
        Assert.Contains("a: Connection refused", alert.Error);
        Assert.NotNull(alert.IncidentId);

        // Recovery: central and a report success -> majority up -> incident closed, UP alert.
        host.Time.Advance(TimeSpan.FromSeconds(30));
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id)); // 1 up, 2 down
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, a, up: true));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.NotNull(Assert.Single(await IncidentsAsync(host, id)).ResolvedAtUtc);
        Assert.Equal(2, _email.Sent.Count);
    }

    [Fact]
    public async Task One_of_three_regions_failing_opens_no_incident()
    {
        var (host, id, a, b) = await ArrangeAsync(confirmBeforeDown: true);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, b, up: true));

        for (var i = 0; i < 3; i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, a, up: false));
            _central.Then(Ok);
            await host.RunCheckAsync(id);
        }
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        Assert.Empty(_email.Sent);
        // Central decided by quorum: no confirmation probe was scheduled.
        Assert.Null((await host.GetMonitorAsync(id)).PendingConfirmationSinceUtc);
    }

    [Fact]
    public async Task Central_failing_alone_is_outvoted_and_uptime_follows_the_vote()
    {
        var (host, id, a, b) = await ArrangeAsync(confirmBeforeDown: true);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        await ReportAsync(host, a, up: true);
        await ReportAsync(host, b, up: true);

        host.Time.Advance(TimeSpan.FromSeconds(60));
        _central.Then(Refused);
        var failed = await host.RunCheckAsync(id);
        Assert.False(failed.IsUp);
        Assert.Equal(true, failed.VotedUp);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        // The other regions are asked to confirm soon.
        Assert.All(await host.WithDbAsync(db => db.MonitorRegionStates.Where(s => s.MonitorId == id).ToListAsync()),
            s => Assert.True(s.NextCheckAtUtc <= host.Now.AddSeconds(10)));

        // Uptime counts the voted status of central checks only (remote results are not counted twice).
        var summary = await host.InScopeAsync(sp => sp.GetRequiredService<IUptimeStatsService>()
            .GetSummariesAsync([id], host.Now.AddHours(-1), host.Now.AddMinutes(1)));
        Assert.Equal(2, summary[id].TotalChecks);
        Assert.Equal(100, summary[id].UptimePercent);
    }

    [Fact]
    public async Task Stale_region_is_excluded_from_the_quorum()
    {
        var (host, id, a, b) = await ArrangeAsync();
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);

        // a reports a failure, then its agent goes silent.
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, a, up: false));
        await host.WithDbAsync(db => RegionTestData.TouchAsync(db, a, host.Now.AddMinutes(-10)));

        // b fails too: counting a's stale vote would make it 2 of 3; without it, 1 of 2 (central up) -> no DOWN.
        host.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, b, up: false));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));

        // Central fails as well: 2 of 2 active regions -> DOWN; the stale region is not listed.
        _central.Then(Refused);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
        Assert.Equal("b,central", Assert.Single(await IncidentsAsync(host, id)).FailedRegions);
    }

    [Fact]
    public async Task Results_taken_before_an_edit_do_not_vote()
    {
        var (host, id, a, b) = await ArrangeAsync();
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, a, up: false));

        await host.WithDbAsync(db => db.Monitors.Where(m => m.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Target, "http://127.0.0.1/fixed").SetProperty(m => m.Version, m => m.Version + 1)));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, b, up: false));
        // a's failure was for the old target: 1 of 2 (central up, b down) -> no DOWN.
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
    }

    [Fact]
    public async Task Without_two_voting_regions_the_single_region_rules_apply()
    {
        var (host, id, a, b) = await ArrangeAsync();
        await using var _ = host;
        await host.WithDbAsync(async db =>
        {
            await RegionTestData.TouchAsync(db, a, host.Now.AddHours(-1));
            await RegionTestData.TouchAsync(db, b, host.Now.AddHours(-1));
        });
        _central.Then(Ok, Refused);
        await host.RunCheckAsync(id);
        host.Time.Advance(TimeSpan.FromSeconds(60));
        var r = await host.RunCheckAsync(id);
        Assert.Null(r.VotedUp);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id)); // threshold 1, no confirmation configured
        Assert.Null(Assert.Single(await IncidentsAsync(host, id)).FailedRegions);
    }

    [Fact]
    public async Task Region_failure_during_maintenance_opens_no_incident()
    {
        var (host, id, a, b) = await ArrangeAsync();
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        await host.WithDbAsync(async db =>
        {
            var monitor = await db.Monitors.Include(m => m.Customer).SingleAsync(m => m.Id == id);
            db.MaintenanceWindows.Add(new MaintenanceWindow
            {
                CustomerId = monitor.CustomerId, Name = "w", StartsAtUtc = host.Now.AddMinutes(-1), EndsAtUtc = host.Now.AddHours(1),
                TimeZoneId = "UTC", Monitors = [monitor]
            });
            await db.SaveChangesAsync();
        });
        await ReportAsync(host, a, up: false);
        await ReportAsync(host, b, up: false);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        Assert.All(await host.WithDbAsync(db => db.CheckResults.Where(c => c.RegionId != null).ToListAsync()), c => Assert.True(c.InMaintenance));
    }

    [Fact]
    public void Tally_rules()
    {
        RegionVote V(string code, bool up) => new(code, up, DateTime.UtcNow, up ? null : "x");
        Assert.True(RegionQuorum.Tally([V("central", false), V("a", false), V("b", true)]).DownMajority);
        Assert.False(RegionQuorum.Tally([V("central", false), V("a", true), V("b", true)]).DownMajority);
        // Two regions: both must agree.
        var split = RegionQuorum.Tally([V("central", false), V("a", true)]);
        Assert.True(split.UsesQuorum);
        Assert.False(split.DownMajority);
        Assert.False(split.UpMajority);
        Assert.False(RegionQuorum.Tally([V("central", false)]).UsesQuorum);
    }
}
