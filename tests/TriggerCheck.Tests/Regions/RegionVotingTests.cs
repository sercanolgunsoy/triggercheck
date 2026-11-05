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

    private Task<MonitoringTestHost> CreateHostAsync(bool confirmBeforeDown) =>
        MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(_central).ReplaceNotifiers(_email),
            new() { ["Monitoring:ConfirmBeforeDown"] = confirmBeforeDown ? "true" : "false" });

    private sealed record Setup(MonitoringTestHost Host, int MonitorId, int CustomerId, TestRegion[] Regions)
    {
        public TestRegion A => Regions[0];
        public TestRegion B => Regions[1];
        public TestRegion C => Regions[2];
    }

    /// <summary>Monitor (60 s) checked from central + <paramref name="regionCount"/> regions; plan allows all regions.</summary>
    private async Task<Setup> ArrangeAsync(bool confirmBeforeDown = false, int threshold = 1, int regionCount = 2)
    {
        var host = await CreateHostAsync(confirmBeforeDown);
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = threshold; m.IntervalSeconds = 60; });
        var regions = await host.WithDbAsync(async db =>
        {
            await RegionTestData.SetPlanRegionsAsync(db, customer.Id, 0);
            var list = new List<TestRegion>();
            for (var i = 0; i < regionCount; i++) list.Add(await RegionTestData.AddRegionAsync(db, ((char)('a' + i)).ToString(), host.Now));
            await RegionTestData.AssignAsync(db, monitor.Id, list.ToArray());
            return list.ToArray();
        });
        return new Setup(host, monitor.Id, customer.Id, regions);
    }

    private static Task<LeaseResponse> LeaseAsync(MonitoringTestHost host, TestRegion region) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ProbeLeaseService>().LeaseAsync(region.Id, 10, CancellationToken.None));

    private static async Task<ResultStatus> IngestAsync(MonitoringTestHost host, TestRegion region, ProbeResult result)
    {
        var acks = await host.InScopeAsync(sp => sp.GetRequiredService<ProbeLeaseService>().IngestAsync(region.Id, [result], CancellationToken.None));
        await host.AlertsIdleAsync();
        return Assert.Single(acks).Status;
    }

    /// <summary>Makes the region's probe due, leases it and reports <paramref name="up"/> for it.</summary>
    private static async Task<ResultStatus> ReportAsync(MonitoringTestHost host, TestRegion region, bool up, string error = "Connection refused")
    {
        await host.WithDbAsync(db => RegionTestData.TouchAsync(db, region, host.Now));
        await host.WithDbAsync(db => db.MonitorRegionStates.Where(s => s.RegionId == region.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextCheckAtUtc, (DateTime?)null)));
        var item = Assert.Single((await LeaseAsync(host, region)).Items);
        return await IngestAsync(host, region, up ? RegionTestData.Up(item.ItemId, host.Now) : RegionTestData.Down(item.ItemId, host.Now, error));
    }

    private static Task<MonitorStatus> StatusAsync(MonitoringTestHost host, int id) =>
        host.WithDbAsync(db => db.Monitors.Where(m => m.Id == id).Select(m => m.Status).SingleAsync());

    private static Task<List<Incident>> IncidentsAsync(MonitoringTestHost host, int id) =>
        host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == id).ToListAsync());

    [Fact]
    public async Task Two_of_three_regions_failing_take_the_monitor_down()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));

        host.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: false));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id)); // central up, a down: no quorum, central decides
        Assert.Empty(await IncidentsAsync(host, id));
        // The failure asks central for a quick confirmation.
        Assert.True((await host.GetMonitorAsync(id)).NextCheckAtUtc <= host.Now.AddSeconds(5));

        host.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: false, error: "Timeout after 10s"));
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

        // Hysteresis: a recovering majority within a minute of the change does not flip the status yet.
        host.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: true));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: true));
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));

        host.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: true));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.NotNull(Assert.Single(await IncidentsAsync(host, id)).ResolvedAtUtc);
        Assert.Equal(2, _email.Sent.Count);
    }

    [Fact]
    public async Task One_of_three_regions_failing_opens_no_incident()
    {
        var s = await ArrangeAsync(confirmBeforeDown: true);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: true));

        for (var i = 0; i < 3; i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: false));
            _central.Then(Ok);
            await host.RunCheckAsync(id);
        }
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        Assert.Empty(_email.Sent);
        Assert.Null((await host.GetMonitorAsync(id)).PendingConfirmationSinceUtc);
    }

    [Fact]
    public async Task Central_failing_alone_is_outvoted_and_uptime_follows_the_vote()
    {
        var s = await ArrangeAsync(confirmBeforeDown: true);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        await ReportAsync(host, s.A, up: true);
        await ReportAsync(host, s.B, up: true);

        host.Time.Advance(TimeSpan.FromSeconds(60));
        _central.Then(Refused);
        var failed = await host.RunCheckAsync(id);
        Assert.False(failed.IsUp);
        Assert.Equal(true, failed.VotedUp);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        // The regions are asked to confirm soon.
        Assert.All(await host.WithDbAsync(db => db.MonitorRegionStates.Where(x => x.MonitorId == id).ToListAsync()),
            x => Assert.True(x.NextCheckAtUtc <= host.Now.AddSeconds(10)));

        // Uptime counts the voted status of central checks only (remote results are not counted twice).
        var summary = await host.InScopeAsync(sp => sp.GetRequiredService<IUptimeStatsService>()
            .GetSummariesAsync([id], host.Now.AddHours(-1), host.Now.AddMinutes(1)));
        Assert.Equal(2, summary[id].TotalChecks);
        Assert.Equal(100, summary[id].UptimePercent);
    }

    [Fact]
    public async Task Stale_region_is_excluded_from_the_quorum()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);

        // a reports a failure, then its agent goes silent.
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: false));
        await host.WithDbAsync(db => RegionTestData.TouchAsync(db, s.A, host.Now.AddMinutes(-10)));

        // b fails too: counting a's stale vote would make it 2 of 3; without it there is no quorum and central (up) decides.
        host.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: false));
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));

        // Central fails as well: its single-region rules take it down (b's failure is the confirmation).
        _central.Then(Refused);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
        Assert.Single(await IncidentsAsync(host, id));
    }

    [Fact]
    public async Task Without_voting_regions_the_single_region_rules_apply()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        await host.WithDbAsync(async db =>
        {
            await RegionTestData.TouchAsync(db, s.A, host.Now.AddHours(-1));
            await RegionTestData.TouchAsync(db, s.B, host.Now.AddHours(-1));
        });
        _central.Then(Ok, Refused);
        await host.RunCheckAsync(id);
        host.Time.Advance(TimeSpan.FromSeconds(60));
        var r = await host.RunCheckAsync(id);
        Assert.Null(r.VotedUp);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id)); // threshold 1, no confirmation configured
        Assert.Null(Assert.Single(await IncidentsAsync(host, id)).FailedRegions);
    }

    // ---- two voters (central + one region): central's single-region rules decide ----

    [Fact]
    public async Task Two_voters_geo_blocked_region_does_not_keep_the_monitor_down()
    {
        var s = await ArrangeAsync(confirmBeforeDown: true, regionCount: 1);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);

        // Real outage: central and a fail. a's failure confirms central's failure right away.
        host.Time.Advance(TimeSpan.FromSeconds(60));
        await ReportAsync(host, s.A, up: false);
        _central.Then(Refused);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));

        // Service back, but a is geo-blocked and keeps failing: central's recovery wins.
        host.Time.Advance(TimeSpan.FromSeconds(60));
        await ReportAsync(host, s.A, up: false);
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.NotNull(Assert.Single(await IncidentsAsync(host, id)).ResolvedAtUtc);
    }

    [Fact]
    public async Task Two_voters_central_failing_and_region_passing_still_alerts()
    {
        var s = await ArrangeAsync(confirmBeforeDown: true, threshold: 2, regionCount: 1);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        await ReportAsync(host, s.A, up: true);

        // Central fails (confirm before DOWN applies, a's success does not suppress it).
        for (var i = 0; i < 3 && await StatusAsync(host, id) != MonitorStatus.Down; i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(60));
            _central.Then(Refused);
            await host.RunCheckAsync(id);
            host.Time.Advance(TimeSpan.FromSeconds(5));
            await host.RunCheckAsync(id); // confirmation
        }
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
        Assert.Contains(_email.Sent, x => x.Message.Kind == TriggerCheck.Monitoring.Alerting.AlertKind.Down);
    }

    [Fact]
    public async Task Two_voters_new_monitor_with_a_failing_region_becomes_up()
    {
        var s = await ArrangeAsync(regionCount: 1);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        await ReportAsync(host, s.A, up: false);
        Assert.Equal(MonitorStatus.Unknown, await StatusAsync(host, id));
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
    }

    [Fact]
    public async Task Two_voters_a_lying_agent_can_neither_take_down_nor_hide_an_outage()
    {
        var s = await ArrangeAsync(regionCount: 1);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;

        // Agent always reports DOWN, central is up: never down.
        for (var i = 0; i < 5; i++)
        {
            await ReportAsync(host, s.A, up: false);
            _central.Then(Ok);
            await host.RunCheckAsync(id);
            host.Time.Advance(TimeSpan.FromSeconds(60));
        }
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));

        // Agent always reports UP, central fails: DOWN anyway.
        await ReportAsync(host, s.A, up: true);
        _central.Then(Refused);
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
    }

    // ---- plan limit (M1) ----

    [Fact]
    public async Task Plan_downgrade_ignores_regions_beyond_the_limit_at_once()
    {
        var s = await ArrangeAsync(regionCount: 3);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        foreach (var r in s.Regions) await ReportAsync(host, r, up: true);

        // 3 -> 2 (central + a): b and c are no longer leased and their UP votes no longer count.
        await host.WithDbAsync(db => RegionTestData.SetPlanRegionsAsync(db, s.CustomerId, 2));
        await host.WithDbAsync(db => db.MonitorRegionStates.ExecuteUpdateAsync(u => u.SetProperty(x => x.NextCheckAtUtc, (DateTime?)null)));
        Assert.Single((await LeaseAsync(host, s.A)).Items);
        Assert.Empty((await LeaseAsync(host, s.B)).Items);
        Assert.Empty((await LeaseAsync(host, s.C)).Items);
        await host.WithDbAsync(db => db.MonitorRegionStates.ExecuteUpdateAsync(u => u.SetProperty(x => x.LeasedUntilUtc, (DateTime?)null).SetProperty(x => x.LeaseItemId, (string?)null)));

        _central.Then(Refused); // central down + a up: two voters, central decides (threshold 1)
        await host.RunCheckAsync(id);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
    }

    [Fact]
    public async Task Plan_downgrade_to_one_region_ignores_all_remote_votes()
    {
        var s = await ArrangeAsync(regionCount: 3);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        foreach (var r in s.Regions) await ReportAsync(host, r, up: true);
        await host.WithDbAsync(db => RegionTestData.SetPlanRegionsAsync(db, s.CustomerId, 1));
        await host.WithDbAsync(db => db.MonitorRegionStates.ExecuteUpdateAsync(u => u.SetProperty(x => x.NextCheckAtUtc, (DateTime?)null)));
        foreach (var r in s.Regions) Assert.Empty((await LeaseAsync(host, r)).Items);

        _central.Then(Refused); // three UP votes would outvote it; ignored, so central's rules take it down
        var result = await host.RunCheckAsync(id);
        Assert.Null(result.VotedUp);
        Assert.Equal(MonitorStatus.Down, await StatusAsync(host, id));
    }

    // ---- flapping and probe rate (M2, M3) ----

    [Fact]
    public async Task Flaky_target_in_three_regions_does_not_flap()
    {
        var s = await ArrangeAsync(threshold: 2);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        // Every region fails every other probe (offset), so somebody always fails but nobody twice in a row.
        for (var round = 0; round < 8; round++)
        {
            _central.Then(round % 2 == 0 ? Ok : Refused);
            await host.RunCheckAsync(id);
            await ReportAsync(host, s.A, up: round % 2 == 1);
            await ReportAsync(host, s.B, up: round % 2 == 0);
            host.Time.Advance(TimeSpan.FromSeconds(60));
        }
        Assert.Empty(await IncidentsAsync(host, id));
        Assert.NotEqual(MonitorStatus.Down, await StatusAsync(host, id));
        Assert.DoesNotContain(_email.Sent, x => x.Message.Kind == TriggerCheck.Monitoring.Alerting.AlertKind.Down);
    }

    [Fact]
    public async Task Expediting_keeps_the_probe_rate_bounded_for_a_flaky_target()
    {
        var s = await ArrangeAsync(threshold: 3);
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        var random = new Random(7);
        var probes = new Dictionary<string, int> { ["central"] = 0, ["a"] = 0, ["b"] = 0 };
        const int intervals = 10;
        var end = host.Now.AddSeconds(60 * intervals);
        while (host.Now < end)
        {
            var m = await host.GetMonitorAsync(id);
            if (m.NextCheckAtUtc is null || m.NextCheckAtUtc <= host.Now)
            {
                _central.Then(random.Next(2) == 0 ? Ok : Refused);
                await host.RunCheckAsync(id);
                probes["central"]++;
            }
            foreach (var r in s.Regions)
            {
                await host.WithDbAsync(db => RegionTestData.TouchAsync(db, r, host.Now));
                foreach (var item in (await LeaseAsync(host, r)).Items)
                {
                    probes[r.Code]++;
                    await IngestAsync(host, r, random.Next(2) == 0 ? RegionTestData.Up(item.ItemId, host.Now) : RegionTestData.Down(item.ItemId, host.Now));
                }
            }
            host.Time.Advance(TimeSpan.FromSeconds(1));
        }
        // At most one regular and one expedited probe per interval in every region.
        Assert.All(probes, p => Assert.True(p.Value <= 2 * intervals + 1, $"{p.Key}: {p.Value} probes in {intervals} intervals"));
        Assert.All(probes, p => Assert.True(p.Value >= intervals, $"{p.Key}: {p.Value} probes"));
    }

    // ---- excluded results (M4) ----

    [Fact]
    public async Task Region_failure_during_maintenance_opens_no_incident()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        await host.WithDbAsync(async db =>
        {
            var monitor = await db.Monitors.SingleAsync(m => m.Id == id);
            db.MaintenanceWindows.Add(new MaintenanceWindow
            {
                CustomerId = monitor.CustomerId, Name = "w", StartsAtUtc = host.Now.AddMinutes(-1), EndsAtUtc = host.Now.AddHours(1), TimeZoneId = "UTC"
            });
            await db.SaveChangesAsync();
        });
        await ReportAsync(host, s.A, up: false);
        await ReportAsync(host, s.B, up: false);
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
        var states = await host.WithDbAsync(db => db.MonitorRegionStates.Where(x => x.MonitorId == id).ToListAsync());
        Assert.All(states, x => Assert.True(x.LastInMaintenance));
        Assert.All(await host.WithDbAsync(db => db.CheckResults.Where(c => c.RegionId != null).ToListAsync()), c => Assert.True(c.InMaintenance));
    }

    [Fact]
    public async Task Results_taken_before_an_edit_do_not_vote()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        _central.Then(Ok);
        await host.RunCheckAsync(id);
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.A, up: false));

        await host.WithDbAsync(db => db.Monitors.Where(m => m.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Target, "http://127.0.0.1/fixed").SetProperty(m => m.Version, m => m.Version + 1)));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: false));
        Assert.Equal(ResultStatus.Accepted, await ReportAsync(host, s.B, up: false));
        // a's failure (old target) and central's success (old target) no longer vote: only b remains -> no quorum.
        Assert.Equal(MonitorStatus.Up, await StatusAsync(host, id));
        Assert.Empty(await IncidentsAsync(host, id));
    }

    [Fact]
    public async Task Central_failing_since_starts_after_the_last_success_or_maintenance_probe()
    {
        var s = await ArrangeAsync();
        var (host, id) = (s.Host, s.MonitorId);
        await using var _ = host;
        var t0 = host.Now.AddMinutes(-10);
        await host.WithDbAsync(async db =>
        {
            CheckResult R(int min, bool up, bool maint = false, int? region = null) =>
                new() { MonitorId = id, CheckedAtUtc = t0.AddMinutes(min), IsUp = up, InMaintenance = maint, RegionId = region };
            db.CheckResults.AddRange(
                R(0, false), R(1, true), R(2, false), R(3, false, maint: true), R(4, false), R(5, false),
                R(6, false, region: s.A.Id)); // remote results are not central
            await db.SaveChangesAsync();
            // Streak starts after the in-maintenance failure (which starts no streak), not at minute 2.
            Assert.Equal(t0.AddMinutes(4), await RegionQuorum.CentralFailingSinceAsync(db, id, t0, CancellationToken.None));
            // Looks back no further than the window.
            Assert.Equal(t0.AddMinutes(5), await RegionQuorum.CentralFailingSinceAsync(db, id, t0.AddMinutes(5), CancellationToken.None));
            // No central failure in the window: the current probe is the first.
            Assert.Null(await RegionQuorum.CentralFailingSinceAsync(db, id, t0.AddMinutes(6), CancellationToken.None));
        });
    }

    [Fact]
    public void Tally_rules()
    {
        var now = DateTime.UtcNow;
        RegionVote V(string code, bool up, bool decisive = true) => new(code, up, decisive, now, now.AddMinutes(-10), up ? null : "x");
        var down = RegionQuorum.Tally([V("central", false), V("a", false), V("b", true)], now.AddMinutes(-2));
        Assert.True(down.UsesQuorum);
        Assert.True(down.DownMajority);
        Assert.Equal(now.AddMinutes(-2), down.FailingSinceUtc); // clamped to the window
        Assert.False(RegionQuorum.Tally([V("central", false), V("a", true), V("b", true)], now).DownMajority);
        // Two voters, or a split: no quorum.
        Assert.False(RegionQuorum.Tally([V("central", false), V("a", false)], now).UsesQuorum);
        Assert.False(RegionQuorum.Tally([V("central", false), V("a", false), V("b", true), V("c", true)], now).UsesQuorum);
        // Failures below the threshold abstain.
        Assert.False(RegionQuorum.Tally([V("central", true), V("a", false, decisive: false), V("b", false, decisive: false)], now).UsesQuorum);
    }
}
