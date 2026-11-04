using System.Text.RegularExpressions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Evals;

/// <summary>
/// Code-graded checks per case (no LLM judge):
/// <list type="bullet">
/// <item><b>primary cause</b>: the most confident hypothesis of the raw output is classified into cause categories by
/// word-boundary patterns (English and Turkish; status codes only next to "HTTP" / "status" / a reason phrase, so
/// "1500 ms" or "check #5004" are no 5xx). The category mentioned first is the primary one and must be accepted for the
/// case's label; a hedge ("maybe DNS or a timeout") naming several categories has no primary cause and fails;</item>
/// <item><b>evidence</b>: every raw hypothesis cites at least one piece of evidence that is grounded in the input: a
/// check id that was sent, a number (2+ digits) or a quoted text / CamelCase identifier that occurs in the input;</item>
/// <item><b>masking</b>: none of the case's planted secrets, monitor names, URL paths or e-mail addresses appear anywhere
/// in the request actually sent (system prompt + user content);</item>
/// <item><b>language</b>: the request asks for the case's language and the summary is written in it;</item>
/// <item><b>no outage statement</b> (AI-3 cases that expect it): the summary says there is no outage yet.</item>
/// </list>
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

    /// <summary>Words that make a multi-category cause a hedge ("maybe DNS or a timeout").</summary>
    private static readonly Regex Hedge = R(@"\bmaybe\b|\bperhaps\b|\bpossibly\b|\beither\b|\bor\b|could be|might be|\bbelki\b|\bveya\b|\bya da\b|olabilir");

    /// <summary>Case label → categories accepted as the primary cause.</summary>
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
        ["keyword_missing"] = ["keyword_missing"],
        ["host_outage"] = ["host_outage"],
        ["maintenance_aftermath"] = ["maintenance"],
        ["slow_dns"] = ["slow_dns"],
        ["slow_tls"] = ["slow_tls"],
        ["slow_connect"] = ["slow_connect"],
        ["slow_ttfb"] = ["slow_ttfb"],
    };

    /// <summary>Categories that only make sense for slowdowns are not counted for incident causes (and vice versa).</summary>
    private static bool IsSlowdownCategory(string name) => name.StartsWith("slow_", StringComparison.Ordinal);

    /// <summary>Matching categories, in the order they are first mentioned.</summary>
    public static List<string> Classify(string? cause, bool slowdown)
    {
        if (string.IsNullOrWhiteSpace(cause)) return [];
        var text = cause.ToLowerInvariant();
        return Categories.Where(c => IsSlowdownCategory(c.Name) == slowdown)
            .Select(c => (c.Name, Index: c.IndexIn(text))).Where(x => x.Index >= 0)
            .OrderBy(x => x.Index).Select(x => x.Name).ToList();
    }

    /// <summary>The primary category of a cause: the first one mentioned; null when none, or when a hedge names several.</summary>
    public static string? PrimaryCategory(string? cause, bool slowdown)
    {
        var categories = Classify(cause, slowdown);
        if (categories.Count == 0) return null;
        if (categories.Count > 1 && Hedge.IsMatch(cause!.ToLowerInvariant())) return null;
        return categories[0];
    }

    /// <summary>True when the label accepts the primary category of <paramref name="cause"/>.</summary>
    public static bool CauseMatches(string? cause, string label, bool slowdown) =>
        PrimaryCategory(cause, slowdown) is { } p && Labels.TryGetValue(label, out var accepted) && accepted.Contains(p);

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex CheckRef();

    [GeneratedRegex(@"['""‘’“”«»]([^'""‘’“”«»]{6,})['""‘’“”«»]")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?<![\d.])\d{2,}(?:\.\d+)?(?![\d])")]
    private static partial Regex Number();

    [GeneratedRegex(@"\b[A-Z][a-z]+(?:[A-Z][a-z]+){2,}\b")]
    private static partial Regex CamelCase();

    /// <summary>Evidence is grounded when it cites a sent check id, or a number / quoted text / identifier present in the input.</summary>
    public static bool Grounded(string? evidence, string input)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        if (CheckRef().Matches(evidence).Any(m => input.Contains("check #" + m.Groups[1].Value + ":", StringComparison.Ordinal))) return true;
        if (Quoted().Matches(evidence).Any(m => input.Contains(m.Groups[1].Value.Trim(), StringComparison.OrdinalIgnoreCase))) return true;
        if (Number().Matches(evidence).Any(m => Regex.IsMatch(input, @"(?<![\d.])" + Regex.Escape(m.Value) + @"(?![\d])"))) return true;
        return CamelCase().Matches(evidence).Any(m => input.Contains(m.Value, StringComparison.Ordinal));
    }

    private static readonly string[] NoOutagePhrases =
    [
        "no outage", "not an outage", "no downtime", "still up", "still available", "remains up", "has not failed", "not down",
        "kesinti yok", "kesinti olmadı", "kesinti henüz", "henüz kesinti", "hâlâ çalış", "hala çalış", "hâlâ erişil", "hala erişil", "erişilebilir durumda"
    ];

    [GeneratedRegex(@"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}")]
    private static partial Regex Email();

    public static EvalCaseResult Grade(EvalScenario s, AiCallRequest? request, AiCallResult? result, AiDecision? decision)
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
            r.Notes.Add("no request was sent");
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

        // ---- primary cause ----
        var slowdown = s.Feature == Domain.AiFeatures.SlowdownExplanation;
        var top = (raw.Hypotheses ?? []).Where(h => h is not null).OrderByDescending(h => h.Confidence).FirstOrDefault();
        r.TopCause = top?.Cause;
        r.Categories = Classify(top?.Cause, slowdown);
        r.PrimaryCauseOk = CauseMatches(top?.Cause, s.Expect.PrimaryCause, slowdown);
        var accepted = Labels.TryGetValue(s.Expect.PrimaryCause, out var a) ? a : [];
        if (!r.PrimaryCauseOk)
            r.Notes.Add($"primary cause {PrimaryCategory(top?.Cause, slowdown) ?? "none"} (mentioned: [{string.Join(", ", r.Categories)}]), expected one of [{string.Join(", ", accepted)}]");

        // ---- evidence: present and grounded in the input ----
        var hypotheses = (raw.Hypotheses ?? []).Where(h => h is not null).ToList();
        r.EvidenceOk = hypotheses.Count > 0 && hypotheses.All(h => (h.Evidence ?? []).Any(e => Grounded(e, request.UserContent)));
        if (!r.EvidenceOk) r.Notes.Add("a hypothesis without evidence grounded in the input (or none at all)");

        // ---- language ----
        r.LanguageOk = requestLanguageOk && LooksTurkish(raw.Summary) == (expectedLanguage == "Turkish");
        if (!r.LanguageOk) r.Notes.Add($"summary is not in {expectedLanguage}");

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
    public List<string> Categories { get; set; } = [];
    public bool PrimaryCauseOk { get; set; }
    public bool EvidenceOk { get; set; }
    public bool MaskingOk { get; set; }
    public bool LanguageOk { get; set; }
    /// <summary>Null when the case does not check it.</summary>
    public bool? NoOutageOk { get; set; }
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

    public bool Passed => PrimaryCauseOk && EvidenceOk && MaskingOk && LanguageOk && NoOutageOk != false;
}
