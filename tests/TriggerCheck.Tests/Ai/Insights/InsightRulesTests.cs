using TriggerCheck.Ai.Insights;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Tests.Ai.Insights;

/// <summary>AI-12 L1 rules (pure): every signal with its thresholds and boundary cases.</summary>
public class InsightRulesTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AiInsightOptions O = new();

    private static InsightCustomer Customer(int id = 1, int maxMonitors = 100, int minInterval = 60, int? maxPages = null, int maxRegions = 1, bool sms = false) =>
        new(id, "Customer " + id, "Standard", maxMonitors, minInterval, maxPages, maxRegions, sms);

    private static InsightMonitor Monitor(int id, int customerId = 1, MonitorStatus status = MonitorStatus.Up, DateTime? changed = null, bool enabled = true,
        int interval = 300, MonitorType type = MonitorType.Http, int regions = 1) =>
        new(id, customerId, "Monitor " + id, type, enabled, status, changed, interval, regions);

    private static InsightInput Input(IEnumerable<InsightCustomer> customers, IEnumerable<InsightMonitor> monitors, IEnumerable<InsightIncident>? incidents = null,
        Dictionary<int, int>? alerts = null, Dictionary<int, int>? pages = null, Dictionary<int, int>? sms = null, int smsLimit = 100,
        Dictionary<int, InsightAiUsage>? ai = null) =>
        new(customers.ToList(), monitors.ToList(), (incidents ?? []).ToList(), alerts ?? new(), pages ?? new(), sms ?? new(), smsLimit, ai ?? new());

    private static IEnumerable<InsightIncident> Incidents(int monitorId, int count, double minutesLong = 60, double hoursAgo = 1) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var start = Now.AddHours(-hoursAgo - i);
            return new InsightIncident(monitorId, start, start.AddMinutes(minutesLong), null);
        });

    private static InsightReport Compute(InsightInput input, AiInsightOptions? o = null) => InsightRules.Compute(input, o ?? O, Now);

    // ---- noise ----

    [Fact]
    public void Noise_needs_the_minimum_incidents_in_the_window()
    {
        var c = Customer();
        Assert.Empty(Compute(Input([c], [Monitor(1)], Incidents(1, O.NoiseMinIncidents - 1))).Noise);
        var s = Assert.Single(Compute(Input([c], [Monitor(1)], Incidents(1, O.NoiseMinIncidents))).Noise);
        Assert.Equal(O.NoiseMinIncidents, s.Incidents);
        Assert.Equal(1, s.Monitors);
        Assert.Equal(1, s.NoisiestMonitor!.Id);
        Assert.Equal(O.NoiseMinIncidents, s.NoisiestMonitor.Incidents);
    }

    [Fact]
    public void Noise_counts_only_incidents_started_inside_the_window()
    {
        var c = Customer();
        var inside = Incidents(1, O.NoiseMinIncidents - 1);
        var edge = new InsightIncident(1, Now.AddDays(-O.WindowDays), Now.AddDays(-O.WindowDays).AddHours(1), null);          // exactly at the start: counted
        var before = new InsightIncident(1, Now.AddDays(-O.WindowDays).AddSeconds(-1), Now.AddDays(-O.WindowDays).AddHours(1), null); // before: not
        Assert.Empty(Compute(Input([c], [Monitor(1)], inside.Append(before))).Noise);
        Assert.Single(Compute(Input([c], [Monitor(1)], inside.Append(edge))).Noise);
    }

    [Fact]
    public void Noise_by_alerts_alone_at_the_alert_threshold()
    {
        var c = Customer();
        Assert.Empty(Compute(Input([c], [Monitor(1)], alerts: new() { [1] = O.NoiseMinAlerts - 1 })).Noise);
        var s = Assert.Single(Compute(Input([c], [Monitor(1)], alerts: new() { [1] = O.NoiseMinAlerts })).Noise);
        Assert.Equal(0, s.Incidents);
        Assert.Equal(O.NoiseMinAlerts, s.Alerts);
        Assert.Null(s.NoisiestMonitor);
    }

    [Fact]
    public void Flaps_are_incidents_resolved_within_the_flap_limit()
    {
        var start = Now.AddHours(-2);
        Assert.True(InsightRules.IsFlap(new(1, start, start.AddMinutes(O.FlapMaxMinutes), null), O.FlapMaxMinutes));
        Assert.False(InsightRules.IsFlap(new(1, start, start.AddMinutes(O.FlapMaxMinutes).AddSeconds(1), null), O.FlapMaxMinutes));
        Assert.False(InsightRules.IsFlap(new(1, start, null, null), O.FlapMaxMinutes)); // still open

        var incidents = Incidents(1, 6, minutesLong: 2).Concat(Incidents(2, 4, minutesLong: 120));
        var s = Assert.Single(Compute(Input([Customer()], [Monitor(1), Monitor(2)], incidents)).Noise);
        Assert.Equal(10, s.Incidents);
        Assert.Equal(6, s.Flaps);
        Assert.Equal(60, s.FlapSharePercent);
        Assert.Equal(5, s.IncidentsPerMonitor);
        Assert.Equal(1, s.NoisiestMonitor!.Id);
    }

    [Fact]
    public void Noise_is_ranked_by_incidents_then_alerts()
    {
        var a = Customer(1);
        var b = Customer(2);
        var c = Customer(3);
        var report = Compute(Input([a, b, c], [Monitor(1, 1), Monitor(2, 2), Monitor(3, 3)],
            Incidents(1, 12).Concat(Incidents(2, 20)).Concat(Incidents(3, 12)), alerts: new() { [3] = 5 }));
        Assert.Equal([2, 3, 1], report.Noise.Select(n => n.CustomerId));
    }

    [Fact]
    public void Rows_are_capped()
    {
        var customers = Enumerable.Range(1, 5).Select(i => Customer(i)).ToList();
        var monitors = customers.Select(c => Monitor(c.Id, c.Id)).ToList();
        var report = Compute(Input(customers, monitors, monitors.SelectMany(m => Incidents(m.Id, 10))), new AiInsightOptions { MaxRows = 3 });
        Assert.Equal(3, report.Noise.Count);
    }

    // ---- plan-limit pressure ----

    [Fact]
    public void Monitor_limit_pressure_starts_at_the_threshold()
    {
        var c = Customer(maxMonitors: 10);
        Assert.Empty(Compute(Input([c], Enumerable.Range(1, 7).Select(i => Monitor(i)))).Pressure);
        var s = Assert.Single(Compute(Input([c], Enumerable.Range(1, 8).Select(i => Monitor(i)))).Pressure);
        var l = Assert.Single(s.Limits);
        Assert.Equal(PlanLimitKind.Monitors, l.Kind);
        Assert.Equal((8d, 10d, 80d), (l.Used, l.Limit, l.Percent));
    }

    [Fact]
    public void Pressure_threshold_is_exact_and_configurable()
    {
        Assert.True(InsightRules.AtLeast(8, 10, 80));
        Assert.False(InsightRules.AtLeast(7.99, 10, 80));
        Assert.False(InsightRules.AtLeast(1, 0, 80)); // no limit: no pressure
        var c = Customer(maxMonitors: 10);
        Assert.Single(Compute(Input([c], Enumerable.Range(1, 5).Select(i => Monitor(i))), new AiInsightOptions { LimitPressurePercent = 50 }).Pressure);
    }

    [Fact]
    public void Status_page_pressure_only_with_a_limit()
    {
        var limited = Customer(1, maxPages: 3);
        var unlimited = Customer(2, maxPages: null);
        var report = Compute(Input([limited, unlimited], [], pages: new() { [1] = 3, [2] = 50 }));
        var s = Assert.Single(report.Pressure);
        Assert.Equal(1, s.CustomerId);
        Assert.Equal(PlanLimitKind.StatusPages, s.Limits.Single().Kind);
        Assert.Equal(100, s.Limits.Single().Percent);
        Assert.Empty(Compute(Input([Customer(1, maxPages: 3)], [], pages: new() { [1] = 2 })).Pressure); // 66.7 %
    }

    [Fact]
    public void Region_pressure_only_on_multi_region_plans()
    {
        Assert.Empty(Compute(Input([Customer(maxRegions: 1)], [Monitor(1, regions: 1)])).Pressure); // central-only plans are always "full"
        Assert.Empty(Compute(Input([Customer(maxRegions: 3)], [Monitor(1, regions: 2)])).Pressure);
        var s = Assert.Single(Compute(Input([Customer(maxRegions: 3)], [Monitor(1, regions: 1), Monitor(2, regions: 3)])).Pressure);
        Assert.Equal((PlanLimitKind.Regions, 3d, 3d), (s.Limits.Single().Kind, s.Limits.Single().Used, s.Limits.Single().Limit));
    }

    [Fact]
    public void Sms_pressure_needs_a_plan_with_sms_and_a_daily_limit()
    {
        Assert.Empty(Compute(Input([Customer(sms: false)], [], sms: new() { [1] = 100 })).Pressure);
        Assert.Empty(Compute(Input([Customer(sms: true)], [], sms: new() { [1] = 79 }, smsLimit: 100)).Pressure);
        Assert.Empty(Compute(Input([Customer(sms: true)], [], sms: new() { [1] = 500 }, smsLimit: 0)).Pressure);
        var s = Assert.Single(Compute(Input([Customer(sms: true)], [], sms: new() { [1] = 80 }, smsLimit: 100)).Pressure);
        Assert.Equal(PlanLimitKind.SmsVoice, s.Limits.Single().Kind);
    }

    [Fact]
    public void Ai_quota_pressure_uses_the_AI10_percent()
    {
        Assert.Empty(Compute(Input([Customer()], [], ai: new() { [1] = new(79, 100, 79.9) })).Pressure);
        Assert.Empty(Compute(Input([Customer()], [], ai: new() { [1] = new(0, 0, 100) })).Pressure); // plan without AI
        var s = Assert.Single(Compute(Input([Customer()], [], ai: new() { [1] = new(10, 100, 85) })).Pressure); // tokens drive the percent
        var l = s.Limits.Single();
        Assert.Equal((PlanLimitKind.AiQuota, 10d, 100d, 85d), (l.Kind, l.Used, l.Limit, l.Percent));
    }

    [Fact]
    public void Fastest_interval_pressure_needs_share_count_and_a_faster_plan()
    {
        // Two of four checked monitors at the plan minimum (60 s): 50 %.
        IEnumerable<InsightMonitor> Mix(int atMin, int slow) =>
            Enumerable.Range(1, atMin).Select(i => Monitor(i, interval: 60)).Concat(Enumerable.Range(100, slow).Select(i => Monitor(i, interval: 300)));
        var s = Assert.Single(Compute(Input([Customer(minInterval: 60)], Mix(2, 2))).Pressure);
        var l = s.Limits.Single();
        Assert.Equal((PlanLimitKind.FastestInterval, 2d, 4d, 50d), (l.Kind, l.Used, l.Limit, l.Percent));

        Assert.Empty(Compute(Input([Customer(minInterval: 60)], Mix(2, 3))).Pressure);   // 40 %
        Assert.Empty(Compute(Input([Customer(minInterval: 60)], Mix(1, 0))).Pressure);   // one monitor is no signal
        Assert.Empty(Compute(Input([Customer(minInterval: 15)], Enumerable.Range(1, 5).Select(i => Monitor(i, interval: 15)))).Pressure); // no faster plan exists
        // Heartbeats and disabled monitors are not counted.
        var extra = new[] { Monitor(50, interval: 300, type: MonitorType.Heartbeat), Monitor(51, interval: 300, enabled: false) };
        Assert.Single(Compute(Input([Customer(minInterval: 60)], Mix(2, 2).Concat(extra))).Pressure);
    }

    [Fact]
    public void Pressure_lists_every_limit_and_ranks_by_the_highest_percent()
    {
        var a = Customer(1, maxMonitors: 10, maxPages: 1);
        var b = Customer(2, maxMonitors: 10);
        var monitors = Enumerable.Range(1, 9).Select(i => Monitor(i, 1)).Concat(Enumerable.Range(20, 10).Select(i => Monitor(i, 2)));
        var report = Compute(Input([a, b], monitors, pages: new() { [1] = 1 }));
        // Both reach 100 %; customer 1 has two limits under pressure (status pages 100 %, monitors 90 %) and comes first.
        Assert.Equal([1, 2], report.Pressure.Select(p => p.CustomerId));
        Assert.Equal(2, report.Pressure.Single(p => p.CustomerId == 1).Limits.Count);
    }

    // ---- abandoned ----

    [Fact]
    public void Abandoned_needs_the_full_down_period()
    {
        var c = Customer();
        var days = O.AbandonedDownDays;
        Assert.Empty(Compute(Input([c], [Monitor(1, status: MonitorStatus.Down, changed: Now.AddDays(-days).AddSeconds(1))])).Abandoned);
        var s = Assert.Single(Compute(Input([c], [Monitor(1, status: MonitorStatus.Down, changed: Now.AddDays(-days))])).Abandoned);
        Assert.Equal(days, s.LongestDownDays);
        Assert.True(s.AllDown);
        Assert.Equal(0, s.AcknowledgementsInPeriod);
    }

    [Fact]
    public void Abandoned_ignores_up_disabled_and_unknown_monitors()
    {
        var old = Now.AddDays(-30);
        var monitors = new[]
        {
            Monitor(1, status: MonitorStatus.Up, changed: old),
            Monitor(2, status: MonitorStatus.Down, changed: old, enabled: false),
            Monitor(3, status: MonitorStatus.Down, changed: null),
            Monitor(4, status: MonitorStatus.Paused, changed: old),
        };
        Assert.Empty(Compute(Input([Customer()], monitors)).Abandoned);
    }

    [Fact]
    public void Abandoned_counts_acknowledgements_of_the_period_and_flags_all_down()
    {
        var c = Customer();
        var monitors = new[] { Monitor(1, status: MonitorStatus.Down, changed: Now.AddDays(-20)), Monitor(2, status: MonitorStatus.Up, changed: Now.AddDays(-1)) };
        var acks = new[]
        {
            new InsightIncident(2, Now.AddDays(-3), Now.AddDays(-3).AddHours(1), Now.AddDays(-3)),                 // inside the period
            new InsightIncident(2, Now.AddDays(-30), Now.AddDays(-30).AddHours(1), Now.AddDays(-O.AbandonedDownDays).AddSeconds(-1)), // before it
        };
        var s = Assert.Single(Compute(Input([c], monitors, acks)).Abandoned);
        Assert.False(s.AllDown);
        Assert.Equal(1, s.AcknowledgementsInPeriod);
        Assert.Equal(2, s.EnabledMonitors);
        Assert.Equal(20, s.DownMonitors.Single().DownDays);
    }

    [Fact]
    public void Abandoned_ranks_all_down_customers_first_then_the_longest_outage()
    {
        var a = Customer(1);
        var b = Customer(2);
        var c = Customer(3);
        var monitors = new[]
        {
            Monitor(1, 1, MonitorStatus.Down, Now.AddDays(-90)), Monitor(2, 1, MonitorStatus.Up, Now.AddDays(-1)), // a: not all down, longest
            Monitor(3, 2, MonitorStatus.Down, Now.AddDays(-10)),                                                   // b: all down
            Monitor(4, 3, MonitorStatus.Down, Now.AddDays(-40)),                                                   // c: all down, longer
        };
        Assert.Equal([3, 2, 1], Compute(Input([a, b, c], monitors)).Abandoned.Select(x => x.CustomerId));
    }

    [Fact]
    public void Empty_platform_has_no_signals()
    {
        var report = Compute(Input([], []));
        Assert.True(report.IsEmpty);
    }
}
