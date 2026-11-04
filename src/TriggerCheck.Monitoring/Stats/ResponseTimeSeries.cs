using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Stats;

/// <summary>
/// One time bucket of the response-time chart. Response-time figures cover successful checks only (a failed probe's
/// duration is usually its timeout); <see cref="Failures"/> counts failed checks. Confirmation probes
/// (<see cref="Domain.Entities.CheckResult.IsConfirmation"/>) are excluded. Phase averages (ms) cover successful HTTP
/// checks with timing data; a phase that did not happen (reused connection, plain http) counts as 0, so the stacked
/// phases add up to the average total. Wait = time to first byte minus DNS/connect/TLS; transfer = total minus time to
/// first byte (body read for Keyword monitors).
/// </summary>
public sealed record SeriesBucket(
    DateTime StartUtc, int Checks, int Failures, double? AvgMs, int? P95Ms,
    double? DnsMs, double? ConnectMs, double? TlsMs, double? WaitMs, double? TransferMs);

/// <param name="P95Approximate">True when p95 was computed over a sample of the checks (large ranges).</param>
public sealed record ResponseTimeSeries(DateTime FromUtc, DateTime ToUtc, TimeSpan BucketSize, IReadOnlyList<SeriesBucket> Buckets, bool P95Approximate = false)
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

    /// <summary>Series for the last <paramref name="range"/>, cached for <see cref="ResponseTimeSeriesService.CacheDuration"/> per (monitor, customer, range).</summary>
    Task<ResponseTimeSeries> GetRecentAsync(int monitorId, int customerId, TimeSpan range, CancellationToken ct = default);
}

/// <summary>
/// Aggregates in the database (one row per non-empty bucket); only p95 needs individual response times. Those are
/// loaded exactly when the range holds at most <see cref="ExactP95MaxRows"/> successful checks, otherwise from an
/// evenly spread sample of about that size (Id modulo N), and the series is flagged <see cref="ResponseTimeSeries.P95Approximate"/>.
/// </summary>
public sealed class ResponseTimeSeriesService(AppDbContext db, IMemoryCache cache, TimeProvider time) : IResponseTimeSeriesService
{
    public const int MaxPoints = 300;
    public const int ExactP95MaxRows = 20_000;
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public async Task<ResponseTimeSeries> GetRecentAsync(int monitorId, int customerId, TimeSpan range, CancellationToken ct = default)
    {
        var key = ("tc:rts", monitorId, customerId, range);
        if (cache.TryGetValue(key, out ResponseTimeSeries? cached) && cached is not null) return cached;
        var now = time.GetUtcNow().UtcDateTime;
        var series = await GetAsync(monitorId, customerId, now - range, now, MaxPoints, ct);
        cache.Set(key, series, CacheDuration);
        return series;
    }

    public async Task<ResponseTimeSeries> GetAsync(int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, int maxPoints = MaxPoints, CancellationToken ct = default)
    {
        if (toUtc <= fromUtc) throw new ArgumentException("toUtc must be after fromUtc.", nameof(toUtc));
        maxPoints = Math.Clamp(maxPoints, 1, MaxPoints);
        // Whole seconds, rounded up so that maxPoints buckets always cover the range.
        var bucketSeconds = Math.Max(1, (long)Math.Ceiling((toUtc - fromUtc).TotalSeconds / maxPoints));
        var bucket = TimeSpan.FromSeconds(bucketSeconds);
        var count = (int)Math.Min(maxPoints, Math.Ceiling((toUtc - fromUtc).Ticks / (double)bucket.Ticks));
        int Index(long raw) => (int)Math.Clamp(raw, 0, count - 1);

        var rows = await SeriesSql.AggregateAsync(db, monitorId, customerId, fromUtc, toUtc, bucketSeconds, ct);
        var aggregates = new BucketRow?[count];
        foreach (var r in rows) aggregates[Index(r.Bucket)] = Merge(aggregates[Index(r.Bucket)], r);

        // p95: exact for small ranges, sampled for large ones.
        var upChecks = rows.Sum(r => r.Checks - r.Failures);
        var sampleEvery = upChecks <= ExactP95MaxRows ? 1 : (int)Math.Ceiling(upChecks / (double)ExactP95MaxRows);
        var timesQuery = db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == monitorId && c.Monitor.CustomerId == customerId && c.CheckedAtUtc >= fromUtc && c.CheckedAtUtc <= toUtc
                        && c.IsUp && !c.IsConfirmation);
        if (sampleEvery > 1) timesQuery = timesQuery.Where(c => c.Id % sampleEvery == 0);
        var times = upChecks == 0 ? [] : await timesQuery.Select(c => new { c.CheckedAtUtc, c.ResponseTimeMs }).ToListAsync(ct);
        var perBucket = new List<int>?[count];
        foreach (var t in times)
            (perBucket[Index((t.CheckedAtUtc - fromUtc).Ticks / bucket.Ticks)] ??= []).Add(t.ResponseTimeMs);

        var buckets = new SeriesBucket[count];
        for (var i = 0; i < count; i++)
            buckets[i] = ToBucket(fromUtc + bucket * i, aggregates[i], perBucket[i]);
        return new ResponseTimeSeries(fromUtc, toUtc, bucket, buckets, sampleEvery > 1);
    }

    /// <summary>Two SQL rows can map to one bucket only through float rounding at the last bucket; merge them.</summary>
    private static BucketRow Merge(BucketRow? a, BucketRow b)
    {
        if (a is null) return b;
        static double? W(double? x, long n, double? y, long m) =>
            x is null ? y : y is null ? x : (x * n + y * m) / (n + m);
        long upA = a.Checks - a.Failures, upB = b.Checks - b.Failures;
        return new BucketRow
        {
            Bucket = a.Bucket, Checks = a.Checks + b.Checks, Failures = a.Failures + b.Failures, Timed = a.Timed + b.Timed,
            AvgMs = W(a.AvgMs, upA, b.AvgMs, upB),
            DnsMs = W(a.DnsMs, a.Timed, b.DnsMs, b.Timed), ConnectMs = W(a.ConnectMs, a.Timed, b.ConnectMs, b.Timed),
            TlsMs = W(a.TlsMs, a.Timed, b.TlsMs, b.Timed), WaitMs = W(a.WaitMs, a.Timed, b.WaitMs, b.Timed),
            TransferMs = W(a.TransferMs, a.Timed, b.TransferMs, b.Timed)
        };
    }

    private static SeriesBucket ToBucket(DateTime start, BucketRow? r, List<int>? times)
    {
        if (r is null) return new SeriesBucket(start, 0, 0, null, null, null, null, null, null, null);
        int? p95 = null;
        if (times is { Count: > 0 })
        {
            times.Sort();
            p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1]; // nearest rank
        }
        static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;
        var timed = r.Timed > 0;
        return new SeriesBucket(start, (int)r.Checks, (int)r.Failures, R(r.AvgMs), p95,
            timed ? R(r.DnsMs) : null, timed ? R(r.ConnectMs) : null, timed ? R(r.TlsMs) : null,
            timed ? R(r.WaitMs) : null, timed ? R(r.TransferMs) : null);
    }
}

/// <summary>Raw per-bucket aggregate as returned by <see cref="SeriesSql"/>.</summary>
public sealed class BucketRow
{
    public long Bucket { get; set; }
    public long Checks { get; set; }
    public long Failures { get; set; }
    public double? AvgMs { get; set; }
    public long Timed { get; set; }
    public double? DnsMs { get; set; }
    public double? ConnectMs { get; set; }
    public double? TlsMs { get; set; }
    public double? WaitMs { get; set; }
    public double? TransferMs { get; set; }
}

/// <summary>
/// The only provider-specific SQL of the chart. Everything is standard SQL except <see cref="BucketIndexSql"/>
/// (SQLite <c>julianday</c>); a PostgreSQL port replaces it with
/// <c>FLOOR(EXTRACT(EPOCH FROM (c."CheckedAtUtc" - @from)) / @bucketSeconds)</c>.
/// </summary>
internal static class SeriesSql
{
    private const string BucketIndexSql = "CAST((julianday(c.\"CheckedAtUtc\") - julianday({2})) * 86400.0 / {4} AS INTEGER)";

    private const string Up = "c.\"IsUp\"";
    private const string Timed = "c.\"IsUp\" AND c.\"TtfbMs\" IS NOT NULL";
    private const string Phases = "COALESCE(c.\"DnsMs\", 0) + COALESCE(c.\"ConnectMs\", 0) + COALESCE(c.\"TlsMs\", 0)";

    private static readonly string Sql = $"""
        SELECT {BucketIndexSql} AS "Bucket",
               COUNT(*) AS "Checks",
               SUM(CASE WHEN {Up} THEN 0 ELSE 1 END) AS "Failures",
               AVG(CASE WHEN {Up} THEN c."ResponseTimeMs" END) AS "AvgMs",
               SUM(CASE WHEN {Timed} THEN 1 ELSE 0 END) AS "Timed",
               AVG(CASE WHEN {Timed} THEN COALESCE(c."DnsMs", 0) END) AS "DnsMs",
               AVG(CASE WHEN {Timed} THEN COALESCE(c."ConnectMs", 0) END) AS "ConnectMs",
               AVG(CASE WHEN {Timed} THEN COALESCE(c."TlsMs", 0) END) AS "TlsMs",
               AVG(CASE WHEN {Timed} THEN CASE WHEN c."TtfbMs" > {Phases} THEN c."TtfbMs" - ({Phases}) ELSE 0 END END) AS "WaitMs",
               AVG(CASE WHEN {Timed} THEN CASE WHEN c."ResponseTimeMs" > c."TtfbMs" THEN c."ResponseTimeMs" - c."TtfbMs" ELSE 0 END END) AS "TransferMs"
        FROM "CheckResults" AS c
        INNER JOIN "Monitors" AS m ON m."Id" = c."MonitorId"
        WHERE c."MonitorId" = {"{0}"} AND m."CustomerId" = {"{1}"} AND c."CheckedAtUtc" >= {"{2}"} AND c."CheckedAtUtc" <= {"{3}"}
              AND NOT c."IsConfirmation"
        GROUP BY {BucketIndexSql}
        """;

    public static Task<List<BucketRow>> AggregateAsync(AppDbContext db, int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, long bucketSeconds, CancellationToken ct) =>
        db.Database.SqlQueryRaw<BucketRow>(Sql, monitorId, customerId, fromUtc, toUtc, (double)bucketSeconds).ToListAsync(ct);
}
