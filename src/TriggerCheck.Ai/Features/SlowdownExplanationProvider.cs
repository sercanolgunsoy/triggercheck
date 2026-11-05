using System.Globalization;
using System.Text.Json;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Features;

/// <summary>
/// AI-3 context: one AI-1 anomaly signal (subject = signal id). Besides the signal's own evidence it gives the model
/// per-phase medians (DNS, TCP connect, TLS, TTFB, total) of the successful checks in the 24 hours before the slowdown
/// and since it started, computed here, so the model only interprets numbers and never produces them.
/// </summary>
public sealed class SlowdownExplanationProvider(AiTenantData data) : IAiContextProvider
{
    public static readonly TimeSpan BaselineWindow = TimeSpan.FromHours(24);
    public const int MaxChecksPerWindow = 2000, MaxPoints = 5;

    public string Feature => AiFeatures.SlowdownExplanation;

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        if (!long.TryParse(job.SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var signalId)) return null;
        var signal = await data.AnomalyAsync(job.CustomerId, signalId, ct);
        if (signal is null) return null;
        var monitor = await data.MonitorAsync(job.CustomerId, signal.MonitorId, ct);
        if (monitor is null) return null;

        // Stored timestamps only (no "now"): the input is stable for an unchanged signal.
        var until = signal.EndedAtUtc ?? signal.UpdatedAtUtc;
        if (until <= signal.StartedAtUtc) until = signal.StartedAtUtc.AddSeconds(1);
        var before = await data.SuccessfulChecksAsync(job.CustomerId, monitor.Id, signal.StartedAtUtc - BaselineWindow, signal.StartedAtUtc, MaxChecksPerWindow, ct);
        var during = await data.SuccessfulChecksAsync(job.CustomerId, monitor.Id, signal.StartedAtUtc, until.AddSeconds(1), MaxChecksPerWindow, ct);
        var openIncident = (await data.MonitorsWithOpenIncidentAsync(job.CustomerId, [monitor.Id], ct)).Count > 0;

        var b = new AiContextBuilder(job.Language).Fact("event", "slowdown_detected");
        IncidentInterpretationProvider.AddMonitor(b, monitor);
        b.Fact("open_incident", openIncident ? "yes" : "no");

        b.Section("Anomaly signal")
            .Identifier("id", signal.Id)
            .Fact("metric", signal.Metric)
            .Fact("started_at", signal.StartedAtUtc)
            .Fact("ended_at", signal.EndedAtUtc is null ? "still open" : IncidentInterpretationProvider.Iso(signal.EndedAtUtc.Value))
            .Fact("peak_robust_z_score", Math.Round(signal.Score, 2));
        foreach (var p in Points(signal.DetailsJson).TakeLast(MaxPoints))
            b.Fact("point", p);

        b.Section("Phase medians of successful checks (ms): 24 hours before vs since the slowdown started");
        b.Fact("checks_before", before.Count).Fact("checks_since", during.Count);
        foreach (var phase in PhaseStats.Compare(before, during))
            b.Fact(phase.Name, phase.Describe());

        return new AiPrompt(AiFeaturePrompts.SlowdownInstructions, b.Build(), AiFeaturePrompts.SlowdownActions,
            AiFeaturePrompts.SlowdownCategories, AiFeaturePrompts.EvidenceKeys(b), AiEvidenceRules.IsAllowed);
    }

    /// <summary>Evidence points written by the baseline job: "at=… ms=… baseline_median_ms=… ratio=… anomalous=…".</summary>
    private static List<string> Points(string? json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) return result;
            foreach (var p in points.EnumerateArray())
            {
                string Get(string name) => p.TryGetProperty(name, out var v) ? v.ToString() : "n/a";
                var at = p.TryGetProperty("at", out var a) && a.TryGetDateTime(out var d) ? IncidentInterpretationProvider.Iso(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : "n/a";
                result.Add($"at={at} ms={Get("ms")} baseline_median_ms={Get("baselineMedianMs")} ratio={Get("ratio")} anomalous={Get("Anomalous").ToLowerInvariant()}");
            }
        }
        catch (JsonException)
        {
            // Evidence is optional context; the phase medians below still describe the slowdown.
        }
        return result;
    }
}

/// <summary>Reads the evidence the baseline job stores on an <see cref="AnomalySignal"/> (raw L1 text when AI is unavailable).</summary>
public static class SlowdownEvidence
{
    /// <summary>Highest "ratio" (observed / baseline median) among the stored points, null when there is none.</summary>
    public static double? PeakRatio(string? detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson);
            if (!doc.RootElement.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) return null;
            double? max = null;
            foreach (var p in points.EnumerateArray())
                if (p.TryGetProperty("ratio", out var r) && r.TryGetDouble(out var v) && v > 0 && (max is null || v > max)) max = v;
            return max;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Per-phase medians of two sets of checks (AI-3 input; also shown as the raw L1 fallback text).</summary>
public static class PhaseStats
{
    public sealed record Phase(string Name, double? BeforeMs, int BeforeCount, double? SinceMs, int SinceCount)
    {
        public double? Ratio => BeforeMs is > 0 && SinceMs is double s ? Math.Round(s / BeforeMs.Value, 2) : null;

        public string Describe() =>
            $"before={Fmt(BeforeMs)} (n={BeforeCount}) since={Fmt(SinceMs)} (n={SinceCount}) ratio={(Ratio is double r ? r.ToString(CultureInfo.InvariantCulture) : "n/a")}";

        private static string Fmt(double? v) => v is double d ? Math.Round(d).ToString(CultureInfo.InvariantCulture) : "n/a";
    }

    private static readonly (string Name, Func<CheckResult, int?> Get)[] Phases =
    [
        ("dns", c => c.DnsMs),
        ("tcp_connect", c => c.ConnectMs),
        ("tls", c => c.TlsMs),
        ("ttfb", c => c.TtfbMs),
        ("total", c => c.ResponseTimeMs),
    ];

    public static List<Phase> Compare(IReadOnlyCollection<CheckResult> before, IReadOnlyCollection<CheckResult> since) =>
        Phases.Select(p =>
        {
            var b = before.Select(p.Get).OfType<int>().ToList();
            var s = since.Select(p.Get).OfType<int>().ToList();
            return new Phase(p.Name, Median(b), b.Count, Median(s), s.Count);
        }).ToList();

    /// <summary>The phase (other than total) with the largest ratio, or null without phase data.</summary>
    public static Phase? MostDegraded(IEnumerable<Phase> phases) =>
        phases.Where(p => p.Name != "total" && p.Ratio is not null).OrderByDescending(p => p.Ratio).FirstOrDefault();

    public static double? Median(IReadOnlyCollection<int> values)
    {
        if (values.Count == 0) return null;
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
    }
}
