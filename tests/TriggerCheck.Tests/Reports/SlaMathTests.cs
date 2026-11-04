using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Tests.Reports;

/// <summary>FEAT-10: pure availability arithmetic.</summary>
public class SlaMathTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime H(double hours) => T0.AddHours(hours);
    private static TimeRange R(double from, double to) => new(H(from), H(to));

    [Fact]
    public void Merge_joins_overlapping_and_touching_ranges()
    {
        var merged = SlaMath.Merge([R(5, 6), R(1, 3), R(2, 4), R(4, 4.5), R(7, 7)]);
        Assert.Equal([R(1, 4.5), R(5, 6)], merged);
    }

    [Fact]
    public void Subtract_cuts_holes()
    {
        var rest = SlaMath.Subtract([R(0, 10)], [R(2, 3), R(5, 12), R(-1, 1)]);
        Assert.Equal([R(1, 2), R(3, 5)], rest);
    }

    [Fact]
    public void Downtime_inside_maintenance_is_not_counted_and_maintenance_is_not_monitored_time()
    {
        // 10 h period, 1 h incident of which 30 min fall into a 2 h maintenance window.
        var a = SlaMath.Compute(H(0), H(10), H(-100), [R(3, 4)], [R(3.5, 5.5)]);
        Assert.Equal(TimeSpan.FromHours(10), a.Monitored);
        Assert.Equal(TimeSpan.FromHours(2), a.Maintenance);
        Assert.Equal(TimeSpan.FromMinutes(30), a.Downtime);
        Assert.Equal(100.0 * (1 - 0.5 / 8), a.UptimePercent!.Value, 6);
    }

    [Fact]
    public void Incident_spanning_the_period_boundaries_is_clipped()
    {
        var a = SlaMath.Compute(H(0), H(24), H(-100), [R(-5, 2), R(23, 30)], []);
        Assert.Equal(TimeSpan.FromHours(3), a.Downtime);
    }

    [Fact]
    public void Monitor_created_mid_period_counts_from_creation()
    {
        var a = SlaMath.Compute(H(0), H(24), H(12), [R(18, 21)], []);
        Assert.Equal(TimeSpan.FromHours(12), a.Monitored);
        Assert.Equal(75.0, a.UptimePercent!.Value, 6);
    }

    [Fact]
    public void Monitor_created_after_the_period_or_fully_in_maintenance_has_no_uptime()
    {
        Assert.Null(SlaMath.Compute(H(0), H(24), H(30), [], []).UptimePercent);
        Assert.Null(SlaMath.Compute(H(0), H(24), H(-1), [R(1, 2)], [R(-5, 40)]).UptimePercent);
    }

    [Fact]
    public void Overlapping_incidents_are_counted_once()
    {
        var a = SlaMath.Compute(H(0), H(10), H(-1), [R(1, 3), R(2, 4)], []);
        Assert.Equal(TimeSpan.FromHours(3), a.Downtime);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(10, 9)]
    [InlineData(20, 18)]
    [InlineData(100, 94)]
    [InlineData(0, -1)]
    public void Percentile_index_is_nearest_rank(int count, int expected) => Assert.Equal(expected, SlaMath.PercentileIndex(count, 95));
}
