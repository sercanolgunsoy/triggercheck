namespace TriggerCheck.Ai.Baseline;

/// <summary>
/// L1 statistics (AI-1), no LLM: robust median + MAD of response times per UTC hour-of-day bucket, robust z-score,
/// and the k-of-n anomaly rule. Pure functions, unit-tested with synthetic data.
/// </summary>
public static class BaselineMath
{
    /// <summary>MAD → standard deviation for normally distributed data.</summary>
    public const double MadToSigma = 1.4826;
    /// <summary>The spread is never assumed smaller than this share of the median (very stable endpoints).</summary>
    public const double MinRelativeSpread = 0.05;

    public readonly record struct Stats(double Median, double Mad, int Count);

    public readonly record struct Sample(DateTime AtUtc, double Ms);

    public static Stats Compute(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return new Stats(0, 0, 0);
        var sorted = values.OrderBy(v => v).ToArray();
        var median = MedianOfSorted(sorted);
        var deviations = sorted.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
        return new Stats(median, MedianOfSorted(deviations), sorted.Length);
    }

    private static double MedianOfSorted(double[] s) =>
        s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;

    /// <summary>Hour buckets 0-23 plus <see cref="Domain.Entities.MonitorBaseline.GlobalBucket"/> over all samples.</summary>
    public static Dictionary<int, Stats> Build(IReadOnlyCollection<Sample> samples)
    {
        var result = samples.GroupBy(s => s.AtUtc.Hour).ToDictionary(g => g.Key, g => Compute(g.Select(s => s.Ms).ToList()));
        result[Domain.Entities.MonitorBaseline.GlobalBucket] = Compute(samples.Select(s => s.Ms).ToList());
        return result;
    }

    /// <summary>The hour bucket when it has enough samples, else the global bucket; null when the global bucket is too small.</summary>
    public static Stats? Reference(IReadOnlyDictionary<int, Stats> baseline, int hour, int minBucketSamples, int minGlobalSamples)
    {
        if (!baseline.TryGetValue(Domain.Entities.MonitorBaseline.GlobalBucket, out var global) || global.Count < minGlobalSamples) return null;
        return baseline.TryGetValue(hour, out var bucket) && bucket.Count >= minBucketSamples ? bucket : global;
    }

    /// <summary>Robust z-score: (x - median) / (1.4826 · MAD), with the spread floored at 5 % of the median (and 1 ms).</summary>
    public static double Score(double value, Stats reference)
    {
        var spread = Math.Max(MadToSigma * reference.Mad, Math.Max(MinRelativeSpread * reference.Median, 1.0));
        return (value - reference.Median) / spread;
    }

    public sealed record Evaluation(bool IsAnomalous, int AnomalousCount, double PeakScore, DateTime? FirstAnomalousAtUtc, IReadOnlyList<Point> Points);

    public sealed record Point(DateTime AtUtc, double Ms, double MedianMs, double Score, bool Anomalous);

    /// <summary>
    /// k-of-n rule over the newest <paramref name="n"/> samples: anomalous when at least <paramref name="k"/> of them
    /// have score &gt; <paramref name="zThreshold"/> and are at least <paramref name="minRatio"/> × the median.
    /// </summary>
    public static Evaluation Evaluate(IReadOnlyDictionary<int, Stats> baseline, IReadOnlyCollection<Sample> recent,
        int k, int n, double zThreshold, double minRatio, int minBucketSamples, int minGlobalSamples)
    {
        var points = new List<Point>();
        foreach (var s in recent.OrderByDescending(s => s.AtUtc).Take(n).OrderBy(s => s.AtUtc))
        {
            if (Reference(baseline, s.AtUtc.Hour, minBucketSamples, minGlobalSamples) is not { } r) continue;
            var score = Score(s.Ms, r);
            points.Add(new Point(s.AtUtc, s.Ms, r.Median, score, score > zThreshold && s.Ms >= r.Median * minRatio));
        }
        var anomalous = points.Where(p => p.Anomalous).ToList();
        return new Evaluation(
            anomalous.Count >= k, anomalous.Count,
            anomalous.Count > 0 ? anomalous.Max(p => p.Score) : 0,
            anomalous.FirstOrDefault()?.AtUtc,
            points);
    }
}
