using System.Text.RegularExpressions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Evals;

/// <summary>
/// Code-graded checks per case (no LLM judge, no keyword matching in pass / fail):
/// <list type="bullet">
/// <item><b>primary cause</b>: the <c>category</c> of the highest-confidence hypothesis that survived validation must be
/// one of the categories accepted for the case's label; a top-confidence tie between different categories is a hedge
/// and fails;</item>
/// <item><b>evidence</b>: every raw hypothesis cites at least one <c>evidence_refs</c> entry that resolves to a key the
/// context provider actually sent and that fits its category (<see cref="AiEvidenceRules"/>: a generic key such as
/// "started_at" grounds nothing), and at least one hypothesis survives validation;</item>
/// <item><b>masking</b>: none of the case's planted secrets, monitor names, URL paths or e-mail addresses appear anywhere
/// in the request actually sent (system prompt + user content);</item>
/// <item><b>language</b>: the request asks for the case's language and the summary is written in it;</item>
/// <item><b>no outage statement</b> (AI-3 cases that expect it): the summary says there is no outage yet.</item>
/// </list>
/// Keyword classification of the cause text (<see cref="Classify"/>) is reported as a soft diagnostic only.
/// </summary>
public static partial class EvalGrader
{
    public const double DefaultThreshold = 0.8;

    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>A cause category: <paramref name="Pattern"/> (and <paramref name="Context"/>, if given) must match the lower-cased text.</summary>
    public sealed record Category(string Name, Regex Pattern, Regex? Context = null)
    {
        /// <summary>Index of the first match, or -1.</summary>
        public int IndexIn(string text)
        {
            if (Context is not null && !Context.IsMatch(text)) return -1;
            var m = Pattern.Match(text);
            return m.Success ? m.Index : -1;
        }
    }

    private static Regex R(string pattern) => new(pattern, Opts);
    private static readonly Regex Certificate = R(@"certificate|\bcert\b|\btls\b|\bssl\b|sertifika");
    private const string StatusWord = @"(?:\bhttp|\bstatus(?: code)?|\bcode|\breturn(?:s|ed)?|\brespond(?:s|ed)? with|\banswer(?:s|ed)? with|durum kodu)\W{1,4}";

    /// <summary>Keyword patterns used only for the "text mentions" diagnostic column.</summary>
    public static readonly IReadOnlyList<Category> Categories =
    [
        new("dns", R(@"\bdns\b|name resolution|\bresolv(?:e|es|ing|er|ution)\b|(?:could|can)(?:not| not|n't) be resolved|nxdomain|\bname ?servers?\b|no such host|alan ad[ıi]|çözümlen|ad çözümle")),
        new("tls_expired", R(@"expired|\bexpiry\b|süresi (?:dol|geç|bit)"), Certificate),
        new("tls_name_mismatch", R(@"mismatch|does not match|doesn't match|wrong (?:host ?)?name|uyuşm|eşleşm"), Certificate),
        new("tls_untrusted", R(@"untrusted|not trusted|self[- ]signed|unknown (?:ca|issuer)|chain (?:error|errors|is (?:broken|incomplete|invalid))|güvenilmeyen|güvenilir değil|kendinden imzalı|zincir hata"), Certificate),
        new("http_5xx", R(@"\b5xx\b|" + StatusWord + @"5\d\d\b|\b5\d\d\W{1,3}(?:internal|bad gateway|service unavailable|gateway timeout|server error|error|hata|durum kodu)|internal server error|bad gateway|service unavailable|gateway timeout|sunucu hatası")),
        new("http_4xx", R(@"\b4xx\b|" + StatusWord + @"4\d\d\b|\b4\d\d\W{1,3}(?:not found|forbidden|unauthori[sz]ed|error|hata|durum kodu)|\bnot found\b|\bforbidden\b|unauthori[sz]ed|bulunamad|yetkisiz|kimlik doğrulama")),
        new("timeout", R(@"\btime(?:d)?[ -]?outs?\b|\btimed out\b|zaman aşım")),
        new("connection_refused", R(@"refus|reddedil|connection reset|unreachable|not listening|erişilemi|erişilemez|dinlemiyor|ulaşılamı")),
        new("keyword_missing", R(@"keyword|expected (?:content|text)|anahtar kelime|beklenen (?:içerik|metin)")),
        new("host_outage", R(@"same host|host-wide|whole host|all monitors|other monitors|entire server|aynı (?:host|sunucu)|tüm monitör|diğer monitör")),
        new("maintenance", R(@"maintenance|bakım")),
        new("slow_dns", R(@"\bdns\b|name resolution|\bname ?servers?\b|ad çözümle|alan ad")),
        new("slow_tls", R(@"\btls\b|handshake|el sıkışma")),
        new("slow_connect", R(@"tcp connect|connect(?:ion)? time|\bnetwork\b|\bağ\b|bağlantı kurma|tcp bağlantı")),
        new("slow_ttfb", R(@"\bttfb\b|time to first byte|first byte|ilk bayt|server processing|server-side|\bapplication\b|uygulama|backend|sunucu tarafı")),
    ];

    /// <summary>Case label → categories (hypotheses[].category) accepted as the primary cause.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Labels = new Dictionary<string, string[]>
    {
        ["dns_failure"] = ["dns"],
        ["tls_expired"] = ["tls_expired"],
        ["tls_name_mismatch"] = ["tls_name_mismatch"],
        ["tls_untrusted"] = ["tls_untrusted"],
        ["http_5xx"] = ["http_5xx"],
        ["http_4xx"] = ["http_4xx"],
        ["timeout"] = ["timeout"],
        ["connection_refused"] = ["connection_refused"],
        ["network_unreachable"] = ["network_unreachable", "connection_refused"],
        ["keyword_missing"] = ["keyword_missing"],
        ["host_outage"] = ["host_outage"],
        ["maintenance_aftermath"] = ["maintenance_related"],
        ["slow_dns"] = ["slow_dns"],
        ["slow_tls"] = ["slow_tls"],
        ["slow_connect"] = ["slow_connect"],
        ["slow_ttfb"] = ["slow_ttfb"],
        // AI-5: the suggestion the rules made (hypotheses[].category is the suggestion code).
        ["raise_failure_threshold"] = ["raise_failure_threshold"],
        ["raise_timeout"] = ["raise_timeout"],
        ["target_may_be_wrong"] = ["target_may_be_wrong"],
        ["review_keyword"] = ["review_keyword"],
        // AI-7: graded by EvalQa (cited records, tools, grounding), not by a cause category.
        [EvalQa.Label] = [],
    };

    /// <summary>Categories that only make sense for slowdowns are not counted for incident causes (and vice versa).</summary>
    private static bool IsSlowdownCategory(string name) => name.StartsWith("slow_", StringComparison.Ordinal);

    /// <summary>Diagnostic only: keyword categories the cause text mentions, in the order they are first mentioned.</summary>
    public static List<string> Classify(string? cause, bool slowdown)
    {
        if (string.IsNullOrWhiteSpace(cause)) return [];
        var text = cause.ToLowerInvariant();
        return Categories.Where(c => IsSlowdownCategory(c.Name) == slowdown)
            .Select(c => (c.Name, Index: c.IndexIn(text))).Where(x => x.Index >= 0)
            .OrderBy(x => x.Index).Select(x => x.Name).ToList();
    }

    /// <summary>The primary hypothesis: highest confidence, the first one on a tie.</summary>
    /// <summary>
    /// The primary hypothesis: the one with the highest confidence. Null when there is none, or when several share the
    /// top confidence with different categories (a hedge: <paramref name="hedged"/> is then true).
    /// </summary>
    public static AiHypothesis? Primary(AiInterpretation? output, out bool hedged)
    {
        hedged = false;
        var list = (output?.Hypotheses ?? []).Where(h => h is not null).ToList();
        if (list.Count == 0) return null;
        var max = list.Max(h => h.Confidence);
        var top = list.Where(h => h.Confidence == max).ToList();
        if (top.Select(h => h.Category).Distinct().Count() > 1)
        {
            hedged = true;
            return null;
        }
        return top[0];
    }

    /// <summary>True when the label accepts the category of the (unhedged) primary hypothesis.</summary>
    public static bool PrimaryMatches(AiInterpretation? output, string label) =>
        Primary(output, out _)?.Category is { } c && Labels.TryGetValue(label, out var accepted) && accepted.Contains(c);

    /// <summary>
    /// At least one evidence_ref resolves to a key the production context provider sent (<see cref="AiPrompt.EvidenceKeys"/>,
    /// never parsed from the text, so untrusted text cannot create keys) and is allowed for the hypothesis' category
    /// (<see cref="AiPrompt.RefAllowed"/>, the same rule the interpreter applies).
    /// </summary>
    public static bool Grounded(AiHypothesis h, AiPrompt prompt) =>
        AiInterpretationValidator.GroundedRefs(h, prompt.EvidenceKeys ?? new HashSet<string>(), prompt.RefAllowed).Any();

    private static readonly string[] NoOutagePhrases =
    [
        "no outage", "not an outage", "no downtime", "still up", "still available", "remains up", "has not failed", "not down",
        "kesinti yok", "kesinti olmadı", "kesinti henüz", "henüz kesinti", "hâlâ çalış", "hala çalış", "hâlâ erişil", "hala erişil", "erişilebilir durumda"
    ];

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    /// <param name="prompt">The AiPrompt the production context provider built for the case (recorded by the harness).</param>
    public static EvalCaseResult Grade(EvalScenario s, AiCallRequest? request, AiCallResult? result, AiDecision? decision, AiPrompt? prompt)
    {
        var r = new EvalCaseResult
        {
            Id = s.Id, Feature = s.Feature, Language = s.Language, Label = s.Expect.PrimaryCause,
            Status = decision?.Status.ToString() ?? "none",
            InputTokens = result?.InputTokens ?? 0, OutputTokens = result?.OutputTokens ?? 0,
            CacheReadTokens = result?.CacheReadTokens ?? 0, CacheWriteTokens = result?.CacheWriteTokens ?? 0,
        };
        r.UserContent = request?.UserContent;
        if (request is null)
        {
            r.Notes.Add(s.Feature == Domain.AiFeatures.SettingsSuggestions && decision is null ? "the rules made no suggestion" : "no request was sent");
            return r;
        }

        // ---- masking (on the request actually sent) ----
        var sent = request.SystemPrompt + "\n" + request.UserContent;
        var leaks = s.Secrets.Where(x => !string.IsNullOrEmpty(x) && sent.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Uri.TryCreate(s.Monitor.Target, UriKind.Absolute, out var target) && target.PathAndQuery.Length > 1 && sent.Contains(target.PathAndQuery, StringComparison.Ordinal))
            leaks.Add(target.PathAndQuery);
        // Monitor names are customer-entered text and never sent (neutral labels instead).
        leaks.AddRange(new[] { s.Monitor.Name }.Concat(s.SameHost.Select(m => m.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n) && sent.Contains(n, StringComparison.Ordinal)));
        leaks.AddRange(Email().Matches(sent).Select(m => m.Value));
        r.MaskingOk = leaks.Count == 0;
        if (!r.MaskingOk) r.Notes.Add("leaked: " + string.Join(", ", leaks.Distinct()));

        var expectedLanguage = s.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        var requestLanguageOk = request.UserContent.StartsWith("response_language: " + expectedLanguage, StringComparison.Ordinal);

        var raw = RawOutput.Parse(result);
        if (raw is null)
        {
            r.Notes.Add("no usable output (" + (result?.ErrorCode ?? result?.Outcome.ToString() ?? "none") + ")");
            return r;
        }
        r.Summary = raw.Summary;

        // ---- primary cause: category of the validated output (what the customer sees) ----
        var slowdown = s.Feature == Domain.AiFeatures.SlowdownExplanation;
        var validated = decision?.Status == AiDecisionStatus.Succeeded ? AiInterpretation.TryParse(decision.OutputJson) : null;
        var top = Primary(validated, out var hedged);
        r.Hedged = hedged;
        r.TopCause = top?.Cause;
        r.PrimaryCategory = top?.Category;
        r.Categories = Classify(top?.Cause, slowdown); // diagnostic only
        r.PrimaryCauseOk = PrimaryMatches(validated, s.Expect.PrimaryCause);
        var accepted = Labels.TryGetValue(s.Expect.PrimaryCause, out var a) ? a : [];
        if (!r.PrimaryCauseOk)
            r.Notes.Add(hedged
                ? "hedged: top-confidence hypotheses with different categories"
                : $"primary category {top?.Category ?? "none"}, expected one of [{string.Join(", ", accepted)}]");

        // ---- evidence: every raw hypothesis has a resolvable evidence_ref; something survives validation ----
        var hypotheses = (raw.Hypotheses ?? []).Where(h => h is not null).ToList();
        var ungrounded = prompt is null ? hypotheses.Count : hypotheses.Count(h => !Grounded(h, prompt));
        r.EvidenceOk = hypotheses.Count > 0 && ungrounded == 0 && (validated?.Hypotheses.Count ?? 0) > 0;
        if (!r.EvidenceOk) r.Notes.Add($"{ungrounded} of {hypotheses.Count} hypotheses without an evidence_ref that was sent and fits their category");

        // ---- language ----
        r.LanguageOk = requestLanguageOk && LooksTurkish(raw.Summary) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"summary is not in {expectedLanguage}");

        // ---- AI-5: every number in the model's text is one of the input's numbers ----
        if (s.Feature == Domain.AiFeatures.SettingsSuggestions)
        {
            var texts = new[] { raw.Summary }.Concat(hypotheses.SelectMany(h => new[] { h.Cause }.Concat(h.Evidence ?? [])))
                .Concat((raw.SuggestedActions ?? []).Select(a => a?.Description)).Concat(raw.Unknowns ?? []);
            var invented = texts.SelectMany(t => AiNumberGrounding.Ungrounded(t, request.UserContent)).Distinct().ToList();
            r.NumbersOk = invented.Count == 0;
            if (!r.NumbersOk.Value) r.Notes.Add("numbers not in the input: " + string.Join(", ", invented));
        }

        // ---- AI-3 ----
        if (s.Expect.NoOutageStatement)
        {
            var summary = raw.Summary.ToLowerInvariant();
            r.NoOutageOk = NoOutagePhrases.Any(p => summary.Contains(p, StringComparison.Ordinal));
            if (r.NoOutageOk == false) r.Notes.Add("summary does not say that there is no outage yet");
        }
        return r;
    }

    /// <summary>Turkish-only letters, or at least two common Turkish words.</summary>
    public static bool LooksTurkish(string text)
    {
        if (text.IndexOfAny(['ğ', 'ı', 'ş', 'İ', 'Ğ', 'Ş']) >= 0) return true;
        var words = text.ToLowerInvariant().Split([' ', ',', '.', ';', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        string[] common = ["ve", "bir", "için", "ile", "bu", "olarak", "nedeniyle", "değil", "hata", "yanıt"];
        return words.Count(w => common.Contains(w)) >= 2;
    }
}

public sealed class EvalCaseResult
{
    public string Id { get; set; } = "";
    public string Feature { get; set; } = "";
    public string Language { get; set; } = "";
    public string Label { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Summary { get; set; }
    public string? TopCause { get; set; }
    /// <summary>Category of the primary hypothesis (graded).</summary>
    public string? PrimaryCategory { get; set; }
    /// <summary>Top-confidence hypotheses with different categories (graded as a failed primary cause).</summary>
    public bool Hedged { get; set; }
    /// <summary>Diagnostic: keyword categories the primary cause text mentions (not graded).</summary>
    public List<string> Categories { get; set; } = [];
    public bool PrimaryCauseOk { get; set; }
    public bool EvidenceOk { get; set; }
    public bool MaskingOk { get; set; }
    public bool LanguageOk { get; set; }
    /// <summary>Null when the case does not check it.</summary>
    public bool? NoOutageOk { get; set; }
    /// <summary>AI-5: no number in the output that is not in the input (null for other features).</summary>
    public bool? NumbersOk { get; set; }
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public decimal CostUsd { get; set; }
    public List<string> Notes { get; set; } = [];
    /// <summary>The user turn actually sent (written to reports/prompts/, not into the JSON report).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UserContent { get; set; }

    public bool Passed => PrimaryCauseOk && EvidenceOk && MaskingOk && LanguageOk && NoOutageOk != false && NumbersOk != false;
}
