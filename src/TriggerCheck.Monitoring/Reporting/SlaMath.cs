namespace TriggerCheck.Monitoring.Reporting;

/// <summary>A half-open time range [StartUtc, EndUtc).</summary>
public readonly record struct TimeRange(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}

/// <summary>Availability of one monitor over one period.</summary>
/// <param name="Monitored">Part of the period after the monitor was created (and before "now").</param>
/// <param name="Maintenance">Part of <paramref name="Monitored"/> covered by maintenance windows.</param>
/// <param name="Downtime">Incident time inside <paramref name="Monitored"/>, maintenance excluded.</param>
public sealed record Availability(TimeSpan Monitored, TimeSpan Maintenance, TimeSpan Downtime)
{
    /// <summary>Uptime in percent of the time outside maintenance; null when there was no such time.</summary>
    public double? UptimePercent
    {
        get
        {
            var counted = Monitored - Maintenance;
            if (counted <= TimeSpan.Zero) return null;
            return Math.Clamp(100.0 * (1 - Downtime.TotalSeconds / counted.TotalSeconds), 0, 100);
        }
    }
}

/// <summary>
/// Pure, time-based SLA arithmetic (FEAT-10, also the status page's daily bars): downtime = incident time, clipped to
/// the period and to the monitor's lifetime, minus maintenance. Free of I/O so it can be unit tested.
/// </summary>
public static class SlaMath
{
    /// <summary>Sorted, non-overlapping ranges (empty ranges dropped).</summary>
    public static List<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var merged = new List<TimeRange>();
        foreach (var r in ranges.Where(r => r.EndUtc > r.StartUtc).OrderBy(r => r.StartUtc))
        {
            if (merged.Count > 0 && r.StartUtc <= merged[^1].EndUtc)
            {
                if (r.EndUtc > merged[^1].EndUtc) merged[^1] = merged[^1] with { EndUtc = r.EndUtc };
            }
            else merged.Add(r);
        }
        return merged;
    }

    /// <summary>Parts of <paramref name="ranges"/> inside <paramref name="window"/>, merged.</summary>
    public static List<TimeRange> Clip(IEnumerable<TimeRange> ranges, TimeRange window) =>
        Merge(ranges.Select(r => new TimeRange(Max(r.StartUtc, window.StartUtc), Min(r.EndUtc, window.EndUtc))));

    /// <summary><paramref name="a"/> minus <paramref name="b"/> (both any order / overlap).</summary>
    public static List<TimeRange> Subtract(IEnumerable<TimeRange> a, IEnumerable<TimeRange> b)
    {
        var cut = Merge(b);
        var result = new List<TimeRange>();
        foreach (var r in Merge(a))
        {
            var start = r.StartUtc;
            foreach (var c in cut)
            {
                if (c.EndUtc <= start) continue;
                if (c.StartUtc >= r.EndUtc) break;
                if (c.StartUtc > start) result.Add(new TimeRange(start, c.StartUtc));
                start = Max(start, c.EndUtc);
                if (start >= r.EndUtc) break;
            }
            if (start < r.EndUtc) result.Add(new TimeRange(start, r.EndUtc));
        }
        return result;
    }

    public static TimeSpan Total(IEnumerable<TimeRange> ranges) => Merge(ranges).Aggregate(TimeSpan.Zero, (t, r) => t + r.Duration);

    /// <summary>
    /// Availability over [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) for a monitor created at
    /// <paramref name="createdUtc"/>. Open incidents are passed with an end of "now" by the caller.
    /// </summary>
    public static Availability Compute(DateTime fromUtc, DateTime toUtc, DateTime createdUtc,
        IEnumerable<TimeRange> incidents, IEnumerable<TimeRange> maintenance)
    {
        var monitored = new TimeRange(Max(fromUtc, createdUtc), toUtc);
        if (monitored.Duration == TimeSpan.Zero) return new Availability(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        var maint = Clip(maintenance, monitored);
        var down = Subtract(Clip(incidents, monitored), maint);
        return new Availability(monitored.Duration, Total(maint), Total(down));
    }

    /// <summary>Nearest-rank percentile index (0-based) of <paramref name="percent"/> in a sorted list of <paramref name="count"/> values.</summary>
    public static int PercentileIndex(int count, double percent) =>
        count <= 0 ? -1 : Math.Clamp((int)Math.Ceiling(percent / 100.0 * count) - 1, 0, count - 1);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
