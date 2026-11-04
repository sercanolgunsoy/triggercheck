using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Stats;

/// <summary>
/// One time bucket of the response-time chart. Response-time figures cover successful checks only (a failed probe's
/// duration is usually its timeout); <see cref="Failures"/> counts failed checks. Confirmation probes
/// (<see cref="Domain.Entities.CheckResult.IsConfirmation"/>) and checks in maintenance (InMaintenance) are excluded. Phase averages (ms) cover successful HTTP
/// checks with timing data; a phase that did not happen (reused connection, plain http) counts as 0, so the stacked
/// phases add up to the average total. Wait = time to first byte minus DNS/connect/TLS; transfer = total minus time to
/// first byte (body read for Keyword monitors).
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

    /// <summary>Series for the last <paramref name="range"/>, cached for <see cref="ResponseTimeSeriesService.CacheDuration"/> per (monitor, customer, range).</summary>
    Task<ResponseTimeSeries> GetRecentAsync(int monitorId, int customerId, TimeSpan range, CancellationToken ct = default);
}

/// <summary>
/// Aggregates entirely in the database: one row per non-empty bucket for the averages/counts and one for the exact
/// nearest-rank p95 (window functions partitioned by bucket), so no individual check rows are loaded.
/// </summary>
public sealed class ResponseTimeSeriesService(AppDbContext db, IMemoryCache cache, TimeProvider time) : IResponseTimeSeriesService
{
    public const int MaxPoints = 300;
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private static (string, int, int, TimeSpan) CacheKey(int monitorId, int customerId, TimeSpan range) => ("tc:rts", monitorId, customerId, range);

    /// <summary>Drops the cached series of one monitor (called by the check runner after it saved a check).</summary>
    public static void Evict(IMemoryCache cache, int monitorId, int customerId, TimeSpan range) =>
        cache.Remove(CacheKey(monitorId, customerId, range));

    public async Task<ResponseTimeSeries> GetRecentAsync(int monitorId, int customerId, TimeSpan range, CancellationToken ct = default)
    {
        var key = CacheKey(monitorId, customerId, range);
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

        var p95 = new int?[count];
        foreach (var r in await SeriesSql.P95Async(db, monitorId, customerId, fromUtc, toUtc, bucketSeconds, ct))
        {
            var i = Index(r.Bucket);
            p95[i] = p95[i] is { } existing ? Math.Max(existing, r.P95Ms) : r.P95Ms; // float-rounding duplicate of the last bucket
        }

        var buckets = new SeriesBucket[count];
        for (var i = 0; i < count; i++)
            buckets[i] = ToBucket(fromUtc + bucket * i, aggregates[i], p95[i]);
        return new ResponseTimeSeries(fromUtc, toUtc, bucket, buckets);
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

    private static SeriesBucket ToBucket(DateTime start, BucketRow? r, int? p95)
    {
        if (r is null) return new SeriesBucket(start, 0, 0, null, null, null, null, null, null, null);
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

/// <summary>Per-bucket p95 as returned by <see cref="SeriesSql.P95Async"/>.</summary>
public sealed class BucketP95Row
{
    public long Bucket { get; set; }
    public int P95Ms { get; set; }
}

/// <summary>
/// The only provider-specific SQL of the chart. Everything is standard SQL except <see cref="BucketIndexSql"/>
/// (SQLite <c>julianday</c>); a PostgreSQL port replaces it with
/// <c>FLOOR(EXTRACT(EPOCH FROM (c."CheckedAtUtc" - @from)) / @bucketSeconds)</c>.
/// </summary>
internal static class SeriesSql
{
    // julianday() is a double with ~40 µs resolution at today's dates: + 1 ms keeps a check exactly on a bucket boundary
    // in the bucket that starts there (as in ResponseTimeSeries.BucketIndexOf) instead of the one before.
    private const string BucketIndexSql = "CAST(((julianday(c.\"CheckedAtUtc\") - julianday({2})) * 86400.0 + 0.001) / {4} AS INTEGER)";

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
              AND NOT c."IsConfirmation" AND NOT c."InMaintenance"
        GROUP BY {BucketIndexSql}
        """;

    // Nearest-rank p95 per bucket: the smallest value whose rank (ascending) is >= 0.95 * count, in integer math.
    private static readonly string P95Sql = $"""
        SELECT t."Bucket" AS "Bucket", MIN(t."Ms") AS "P95Ms"
        FROM (
            SELECT {BucketIndexSql} AS "Bucket", c."ResponseTimeMs" AS "Ms",
                   ROW_NUMBER() OVER (PARTITION BY {BucketIndexSql} ORDER BY c."ResponseTimeMs") AS "Rank",
                   COUNT(*) OVER (PARTITION BY {BucketIndexSql}) AS "Total"
            FROM "CheckResults" AS c
            INNER JOIN "Monitors" AS m ON m."Id" = c."MonitorId"
            WHERE c."MonitorId" = {"{0}"} AND m."CustomerId" = {"{1}"} AND c."CheckedAtUtc" >= {"{2}"} AND c."CheckedAtUtc" <= {"{3}"}
                  AND c."IsUp" AND NOT c."IsConfirmation" AND NOT c."InMaintenance"
        ) AS t
        WHERE t."Rank" * 100 >= t."Total" * 95
        GROUP BY t."Bucket"
        """;

    public static Task<List<BucketP95Row>> P95Async(AppDbContext db, int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, long bucketSeconds, CancellationToken ct) =>
        db.Database.SqlQueryRaw<BucketP95Row>(P95Sql, monitorId, customerId, fromUtc, toUtc, (double)bucketSeconds).ToListAsync(ct);

    public static Task<List<BucketRow>> AggregateAsync(AppDbContext db, int monitorId, int customerId, DateTime fromUtc, DateTime toUtc, long bucketSeconds, CancellationToken ct) =>
        db.Database.SqlQueryRaw<BucketRow>(Sql, monitorId, customerId, fromUtc, toUtc, (double)bucketSeconds).ToListAsync(ct);
}
