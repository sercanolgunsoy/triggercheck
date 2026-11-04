using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Baseline;
using TriggerCheck.Domain.Entities;
using Sample = TriggerCheck.Ai.Baseline.BaselineMath.Sample;

namespace TriggerCheck.Tests.Ai;

/// <summary>
/// AI-1 acceptance with synthetic data: a 3x slowdown is flagged within 3 checks; the normal day/night pattern gives
/// fewer than 2 % false positives.
/// </summary>
public class BaselineTests
{
    private static readonly AiBaselineOptions O = new();
    private static readonly DateTime Day0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Day/night pattern: ~100 ms at 03:00 UTC, ~300 ms at 15:00 UTC.</summary>
    private static double Expected(DateTime at)
    {
        var h = at.Hour + at.Minute / 60.0;
        return 200 - 100 * Math.Cos(2 * Math.PI * (h - 3) / 24);
    }

    /// <summary>Log-normal noise (sigma 0.1) plus isolated 2.5x spikes on ~1 % of the checks.</summary>
    private static List<Sample> Generate(Random rnd, DateTime from, DateTime to, TimeSpan step, Func<DateTime, double>? factor = null)
    {
        var list = new List<Sample>();
        for (var t = from; t < to; t += step)
        {
            var u1 = 1.0 - rnd.NextDouble();
            var u2 = rnd.NextDouble();
            var gauss = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            var ms = Expected(t) * Math.Exp(0.1 * gauss);
            if (rnd.NextDouble() < 0.01) ms *= 2.5;
            list.Add(new Sample(t, Math.Round(ms * (factor?.Invoke(t) ?? 1))));
        }
        return list;
    }

    private static Dictionary<int, BaselineMath.Stats> WeekBaseline(Random rnd) =>
        BaselineMath.Build(Generate(rnd, Day0, Day0.AddDays(7), TimeSpan.FromMinutes(5)));

    private static BaselineMath.Evaluation Eval(Dictionary<int, BaselineMath.Stats> b, IReadOnlyCollection<Sample> recent, int minBucket = -1) =>
        BaselineMath.Evaluate(b, recent, O.K, O.N, O.ZThreshold, O.MinRatio, minBucket < 0 ? O.MinBucketSamples : minBucket, O.MinGlobalSamples);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Normal_day_night_pattern_gives_under_2_percent_false_positives(int seed)
    {
        var rnd = new Random(seed);
        var baseline = WeekBaseline(rnd);
        // Day 8 with 1-minute checks; the k-of-n rule is evaluated after every check.
        var day = Generate(rnd, Day0.AddDays(7), Day0.AddDays(8), TimeSpan.FromMinutes(1));
        var flagged = 0;
        for (var i = O.N; i <= day.Count; i++)
            if (Eval(baseline, day.GetRange(i - O.N, O.N)).IsAnomalous) flagged++;
        var rate = flagged / (double)(day.Count - O.N + 1);
        Assert.True(rate < 0.02, $"false positive rate {rate:P2}");
    }

    [Theory]
    [InlineData(3)]   // night: 100 ms -> 300 ms (only the hour-of-day baseline sees this)
    [InlineData(10)]
    [InlineData(15)]  // afternoon peak: 300 ms -> 900 ms
    public void Three_x_slowdown_is_flagged_within_three_checks(int hour)
    {
        var rnd = new Random(42);
        var baseline = WeekBaseline(rnd);
        var start = Day0.AddDays(7).AddHours(hour);
        var before = Generate(rnd, start.AddMinutes(-10), start, TimeSpan.FromMinutes(1));
        var slow = Generate(rnd, start, start.AddMinutes(10), TimeSpan.FromMinutes(1), _ => 3);
        var series = before.Concat(slow).ToList();

        int? flaggedAfter = null;
        for (var i = before.Count + 1; i <= series.Count; i++)
        {
            var e = Eval(baseline, series.GetRange(i - O.N, O.N));
            if (!e.IsAnomalous) continue;
            flaggedAfter = i - before.Count;
            Assert.True(e.FirstAnomalousAtUtc >= start);
            Assert.True(e.PeakScore > O.ZThreshold);
            break;
        }
        Assert.NotNull(flaggedAfter);
        Assert.True(flaggedAfter <= 3, $"flagged after {flaggedAfter} slow checks");
    }

    [Fact]
    public void Night_slowdown_needs_the_hour_of_day_buckets()
    {
        var rnd = new Random(7);
        var baseline = WeekBaseline(rnd);
        var start = Day0.AddDays(7).AddHours(3);
        var slow = Generate(rnd, start, start.AddMinutes(5), TimeSpan.FromMinutes(1), _ => 3);
        Assert.True(Eval(baseline, slow).IsAnomalous);
        Assert.False(Eval(baseline, slow, minBucket: int.MaxValue).IsAnomalous); // global median only: looks like a normal afternoon
    }

    [Fact]
    public void Sparse_buckets_fall_back_to_global_and_tiny_baselines_are_not_evaluated()
    {
        var samples = Enumerable.Range(0, 60).Select(i => new Sample(Day0.AddMinutes(i), 100 + i % 5)).ToList(); // all in hour 0
        var b = BaselineMath.Build(samples);
        Assert.Equal(60, b[MonitorBaseline.GlobalBucket].Count);
        Assert.Equal(b[MonitorBaseline.GlobalBucket], BaselineMath.Reference(b, 13, 20, 50));
        Assert.Equal(b[0], BaselineMath.Reference(b, 0, 20, 50));
        Assert.Null(BaselineMath.Reference(b, 0, 20, 100));

        var stats = BaselineMath.Compute([1, 2, 3, 4, 100]);
        Assert.Equal(3, stats.Median);
        Assert.Equal(1, stats.Mad);
    }

    [Fact]
    public async Task Job_opens_updates_and_closes_an_anomaly_signal_from_check_results()
    {
        await using var h = await AiTestHost.CreateAsync();
        var c = await h.AddCustomerAsync();
        var m = await h.AddMonitorAsync(c.Id, x => x.IntervalSeconds = 300);
        var other = await h.AddMonitorAsync(c.Id, x => x.IntervalSeconds = 300);
        var rnd = new Random(5);
        var now = h.Now;

        async Task AddChecksAsync(int monitorId, IEnumerable<Sample> samples, bool isUp = true) =>
            await h.WithDbAsync(async db =>
            {
                db.CheckResults.AddRange(samples.Select(s => new CheckResult
                {
                    MonitorId = monitorId, CheckedAtUtc = s.AtUtc, IsUp = isUp, ResponseTimeMs = (int)s.Ms, StatusCode = isUp ? 200 : 503
                }));
                await db.SaveChangesAsync();
            });
        Task<BaselineJob.RunSummary> RunJobAsync() => h.InScopeAsync(sp => sp.GetRequiredService<BaselineJob>().RunAsync());

        var week = Generate(rnd, now.AddDays(-7), now, TimeSpan.FromMinutes(5));
        await AddChecksAsync(m.Id, week);
        await AddChecksAsync(other.Id, week);
        // Failed checks never enter the baseline.
        await AddChecksAsync(m.Id, [new Sample(now.AddMinutes(-1), 30_000)], isUp: false);

        var first = await RunJobAsync();
        Assert.Equal((2, 2, 0), (first.Monitors, first.BaselinesRebuilt, first.SignalsOpened));
        var rows = await h.WithDbAsync(db => db.MonitorBaselines.Where(b => b.MonitorId == m.Id).ToListAsync());
        Assert.Equal(25, rows.Count); // 24 hour buckets + global
        Assert.All(rows, r => Assert.Equal(c.Id, r.CustomerId));
        Assert.True(rows.Single(r => r.Bucket == MonitorBaseline.GlobalBucket).MedianMs < 1000);

        // Two slow checks: not yet an anomaly.
        var slowStart = now.AddMinutes(5);
        await AddChecksAsync(m.Id, Generate(rnd, slowStart, slowStart.AddMinutes(10), TimeSpan.FromMinutes(5), _ => 3));
        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, (await RunJobAsync()).SignalsOpened);

        // Third slow check: flagged, starting at the first slow check.
        await AddChecksAsync(m.Id, Generate(rnd, slowStart.AddMinutes(10), slowStart.AddMinutes(15), TimeSpan.FromMinutes(5), _ => 3));
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, (await RunJobAsync()).SignalsOpened);
        var signal = await h.WithDbAsync(db => db.AnomalySignals.SingleAsync());
        Assert.Equal((m.Id, c.Id, BaselineJob.ResponseTimeMetric), (signal.MonitorId, signal.CustomerId, signal.Metric));
        Assert.Equal(slowStart, signal.StartedAtUtc);
        Assert.Null(signal.EndedAtUtc);
        Assert.True(signal.Score > O.ZThreshold);
        Assert.Contains("\"ratio\"", signal.DetailsJson);

        // Re-running on the same data changes nothing (idempotent).
        var again = await RunJobAsync();
        Assert.Equal((0, 0), (again.SignalsOpened, again.SignalsClosed));

        // Back to normal: closed at the newest normal check.
        var normalStart = slowStart.AddMinutes(15);
        var normal = Generate(rnd, normalStart, normalStart.AddMinutes(25), TimeSpan.FromMinutes(5));
        await AddChecksAsync(m.Id, normal);
        h.Time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(1, (await RunJobAsync()).SignalsClosed);
        signal = await h.WithDbAsync(db => db.AnomalySignals.SingleAsync());
        Assert.Equal(normal[^1].AtUtc, signal.EndedAtUtc);

        // Monitoring state is untouched by L1.
        var monitor = await h.WithDbAsync(db => db.Monitors.AsNoTracking().SingleAsync(x => x.Id == m.Id));
        Assert.Equal(MonitorStatus.Unknown, monitor.Status);
        Assert.Equal(0, await h.WithDbAsync(db => db.Incidents.CountAsync()));
    }

    [Fact]
    public async Task Job_skips_disabled_monitors_and_suspended_customers()
    {
        await using var h = await AiTestHost.CreateAsync();
        var c = await h.AddCustomerAsync();
        await h.AddMonitorAsync(c.Id, x => x.IsEnabled = false);
        var suspended = await h.AddCustomerAsync();
        await h.WithDbAsync(db => db.Customers.Where(x => x.Id == suspended.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false)));
        await h.AddMonitorAsync(suspended.Id);

        var r = await h.InScopeAsync(sp => sp.GetRequiredService<BaselineJob>().RunAsync());
        Assert.Equal(0, r.Monitors);
    }
}
