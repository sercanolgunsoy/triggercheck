using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TriggerCheck.Evals;

/// <summary>Scores of one eval run, per feature and per case, with tokens and the estimated cost.</summary>
public sealed class EvalReport
{
    public string Mode { get; set; } = "fake";
    public double Threshold { get; set; } = EvalGrader.DefaultThreshold;
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public List<EvalCaseResult> Cases { get; set; } = [];

    public sealed record FeatureScore(string Feature, int Cases, double PrimaryCauseAccuracy, double EvidenceRate, double LanguageRate,
        int MaskingFailures, int Passed, long InputTokens, long OutputTokens, decimal CostUsd, bool GatePassed);

    private static double Rate(IReadOnlyCollection<EvalCaseResult> c, Func<EvalCaseResult, bool> f) => c.Count == 0 ? 0 : (double)c.Count(f) / c.Count;

    public List<FeatureScore> Features => Cases.GroupBy(c => c.Feature).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
    {
        var c = g.ToList();
        var accuracy = Rate(c, x => x.PrimaryCauseOk);
        var evidence = Rate(c, x => x.EvidenceOk);
        var language = Rate(c, x => x.LanguageOk);
        var masking = c.Count(x => !x.MaskingOk);
        var noOutage = c.Where(x => x.NoOutageOk is not null).ToList();
        var gate = accuracy >= Threshold && evidence >= Threshold && language >= Threshold && masking == 0
                   && (noOutage.Count == 0 || Rate(noOutage, x => x.NoOutageOk == true) >= Threshold)
                   && c.All(x => x.NumbersOk != false); // AI-5: an invented number is never acceptable
        return new FeatureScore(g.Key, c.Count, accuracy, evidence, language, masking, c.Count(x => x.Passed),
            c.Sum(x => x.InputTokens + x.CacheReadTokens + x.CacheWriteTokens), c.Sum(x => x.OutputTokens), c.Sum(x => x.CostUsd), gate);
    }).ToList();

    /// <summary>The quality gate: every feature that has cases passes (see evals/README.md).</summary>
    public bool GatePassed => Cases.Count > 0 && Features.All(f => f.GatePassed);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(new { Mode, Threshold, StartedAtUtc, FinishedAtUtc, GatePassed, Features, Cases }, Json);

    public string ToMarkdown()
    {
        string P(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
        string Ok(bool v) => v ? "ok" : "**FAIL**";
        var sb = new StringBuilder();
        sb.AppendLine("# AI eval report").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Mode: **{Mode}**{(Mode == "fake" ? " (canned outputs: checks the harness, not model quality)" : "")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Run: {StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC, {(FinishedAtUtc - StartedAtUtc).TotalSeconds:0.0} s");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Threshold: {P(Threshold)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Gate: **{(GatePassed ? "PASSED" : "FAILED")}**").AppendLine();

        sb.AppendLine("| Feature | Cases | Primary cause | Evidence | Language | Masking failures | Passed | Input tokens | Output tokens | Cost (USD) | Gate |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var f in Features)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {f.Feature} | {f.Cases} | {P(f.PrimaryCauseAccuracy)} | {P(f.EvidenceRate)} | {P(f.LanguageRate)} | {f.MaskingFailures} | {f.Passed} | {f.InputTokens} | {f.OutputTokens} | {f.CostUsd:0.0000} | {(f.GatePassed ? "passed" : "**failed**")} |");
        sb.AppendLine();

        sb.AppendLine("| Case | Lang | Label | Primary category | Cause | Evidence | Masking | Language | Text mentions (diagnostic) | Tokens in/out | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var c in Cases)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Id} | {c.Language} | {c.Label} | {(c.Hedged ? "**hedged**" : c.PrimaryCategory)} | {Ok(c.PrimaryCauseOk)} | {Ok(c.EvidenceOk)} | {Ok(c.MaskingOk)} | {Ok(c.LanguageOk)} | {string.Join(", ", c.Categories)} | {c.InputTokens + c.CacheReadTokens + c.CacheWriteTokens}/{c.OutputTokens} | {string.Join("; ", c.Notes).Replace("|", "/")} |");
        return sb.ToString();
    }

    /// <summary>Writes last-report.json, last-report.md and prompts/&lt;case&gt;.txt (the user turn sent) to <paramref name="dir"/> (evals/reports, gitignored).</summary>
    public void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "last-report.json"), ToJson());
        File.WriteAllText(Path.Combine(dir, "last-report.md"), ToMarkdown());
        var prompts = Path.Combine(dir, "prompts");
        Directory.CreateDirectory(prompts);
        foreach (var c in Cases.Where(c => c.UserContent is not null))
            File.WriteAllText(Path.Combine(prompts, c.Id + ".txt"), c.UserContent);
    }
}
