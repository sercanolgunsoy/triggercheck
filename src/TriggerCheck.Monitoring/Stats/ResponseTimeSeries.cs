using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Stats;

/// <summary>
/// One time bucket of the response-time chart. Response-time figures cover successful checks only (a failed probe's
/// duration is usually its timeout); <see cref="Failures"/> counts failed checks. Phase averages (ms) cover successful
/// HTTP checks with timing data; a phase that did not happen (reused connection, plain http) counts as 0, so the
/// stacked phases add up to the average total. Wait = time to first byte minus DNS/connect/TLS; transfer = total
/// minus time to first byte (body read for Keyword monitors).
/// </summary>
public sealed record SeriesBucket(
    DateTime StartUtc, int Checks, int Failures, double? AvgMs, int? P95Ms,
    double? DnsMs, double? ConnectMs, double? TlsMs, double? WaitMs, double? TransferMs);

public sealed record ResponseTimeSeries(DateTime FromUtc, DateTime ToUtc, TimeSpan BucketSize, IReadOnlyList<SeriesBucket> Buckets)
{
    /// <summary>Index of the bucket containing <paramref name="utc"/>, or null when outside the range.</summary>
    public int? BucketIndexOf(DateTime utc)
    {
        if (utc < FromUtc || utc > ToUtc) return null;
        return (int)Math.Min(Buckets.Count - 1, (utc - FromUtc).Ticks / BucketSize.Ticks);
    }
}

public interface IResponseTimeSeriesService
{
    /// <summary>
    /// Response-time series of one monitor of <paramref name="customerId"/> over [fromUtc, toUtc], in equal time
    /// buckets (at most <paramref name="maxPoints"/>, empty buckets included). Another customer's monitor yields only empty buckets.
    /// </summary>
    Task<ResponseTimeSeries> GetAsync(int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, int maxPoints = ResponseTimeSeriesService.MaxPoints, CancellationToken ct = default);
}

public sealed class ResponseTimeSeriesService(AppDbContext db) : IResponseTimeSeriesService
{
    public const int MaxPoints = 300;

    public async Task<ResponseTimeSeries> GetAsync(int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, int maxPoints = MaxPoints, CancellationToken ct = default)
    {
        if (toUtc <= fromUtc) throw new ArgumentException("toUtc must be after fromUtc.", nameof(toUtc));
        maxPoints = Math.Clamp(maxPoints, 1, MaxPoints);
        // Whole seconds, rounded up so that maxPoints buckets always cover the range.
        var bucketSeconds = Math.Max(1, (long)Math.Ceiling((toUtc - fromUtc).TotalSeconds / maxPoints));
        var bucket = TimeSpan.FromSeconds(bucketSeconds);
        var count = (int)Math.Min(maxPoints, Math.Ceiling((toUtc - fromUtc).Ticks / (double)bucket.Ticks));

        var rows = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == monitorId && c.Monitor.CustomerId == customerId && c.CheckedAtUtc >= fromUtc && c.CheckedAtUtc <= toUtc)
            .Select(c => new Row(c.CheckedAtUtc, c.IsUp, c.ResponseTimeMs, c.DnsMs, c.ConnectMs, c.TlsMs, c.TtfbMs))
            .ToListAsync(ct);

        var groups = new List<Row>?[count];
        foreach (var r in rows)
        {
            var i = (int)Math.Min(count - 1, (r.At - fromUtc).Ticks / bucket.Ticks);
            (groups[i] ??= []).Add(r);
        }

        var buckets = new SeriesBucket[count];
        for (var i = 0; i < count; i++)
            buckets[i] = Summarize(fromUtc + bucket * i, groups[i]);
        return new ResponseTimeSeries(fromUtc, toUtc, bucket, buckets);
    }

    private sealed record Row(DateTime At, bool IsUp, int Ms, int? Dns, int? Connect, int? Tls, int? Ttfb);

    private static SeriesBucket Summarize(DateTime start, List<Row>? rows)
    {
        if (rows is null) return new SeriesBucket(start, 0, 0, null, null, null, null, null, null, null);
        var up = rows.Where(r => r.IsUp).ToList();
        var failures = rows.Count - up.Count;
        if (up.Count == 0) return new SeriesBucket(start, rows.Count, failures, null, null, null, null, null, null, null);

        var sorted = up.Select(r => r.Ms).Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1]; // nearest-rank

        var timed = up.Where(r => r.Ttfb is not null).ToList();
        double? Avg(Func<Row, double> f) => timed.Count == 0 ? null : Math.Round(timed.Average(f), 1);
        return new SeriesBucket(start, rows.Count, failures, Math.Round(up.Average(r => r.Ms), 1), p95,
            Avg(r => r.Dns ?? 0),
            Avg(r => r.Connect ?? 0),
            Avg(r => r.Tls ?? 0),
            Avg(r => Math.Max(0, r.Ttfb!.Value - (r.Dns ?? 0) - (r.Connect ?? 0) - (r.Tls ?? 0))),
            Avg(r => Math.Max(0, r.Ms - r.Ttfb!.Value)));
    }
}
