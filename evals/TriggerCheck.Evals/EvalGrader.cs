using System.Text.RegularExpressions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Evals;

/// <summary>
/// Code-graded checks per case (no LLM judge):
/// <list type="bullet">
/// <item><b>primary cause</b>: the most confident hypothesis of the raw output is classified into cause categories by
/// keywords (English and Turkish); it passes when one of the categories accepted for the case's label matches and at
/// most two categories match in total (a vague "DNS, TLS or a timeout" answer does not pass);</item>
/// <item><b>evidence</b>: every raw hypothesis cites at least one piece of evidence;</item>
/// <item><b>masking</b>: none of the case's planted secrets, URL paths or e-mail addresses appear anywhere in the request
/// actually sent (system prompt + user content);</item>
/// <item><b>language</b>: the request asks for the case's language and the summary is written in it;</item>
/// <item><b>no outage statement</b> (AI-3 cases that expect it): the summary says there is no outage yet.</item>
/// </list>
/// </summary>
public static partial class EvalGrader
{
    public const double DefaultThreshold = 0.8;

    /// <summary>A cause category: matches when the text contains one of <paramref name="Any"/> and, if given, one of <paramref name="AlsoAny"/>.</summary>
    public sealed record Category(string Name, string[] Any, string[]? AlsoAny = null)
    {
        public bool Matches(string text) =>
            Any.Any(k => text.Contains(k, StringComparison.Ordinal)) && (AlsoAny is null || AlsoAny.Any(k => text.Contains(k, StringComparison.Ordinal)));
    }

    private static readonly string[] Certificate = ["certificate", "cert", "tls", "ssl", "sertifika"];

    public static readonly IReadOnlyList<Category> Categories =
    [
        new("dns", ["dns", "name resolution", "resolve", "nxdomain", "name server", "nameserver", "alan adı", "ad çözümle", "çözümlen", "çözümle"]),
        new("tls_expired", ["expired", "expiry", "süresi dol", "süresi geç", "süresi bit"], Certificate),
        new("tls_name_mismatch", ["mismatch", "does not match", "doesn't match", "wrong name", "host name", "hostname", "uyuşm", "eşleşm"], Certificate),
        new("tls_untrusted", ["untrusted", "self-signed", "self signed", "chain", "trusted", "güvenil", "zincir", "kendinden imzalı"], Certificate),
        new("http_5xx", ["5xx", "500", "502", "503", "504", "server error", "bad gateway", "service unavailable", "gateway timeout", "sunucu hatası", "sunucu taraflı"]),
        new("http_4xx", ["4xx", "401", "403", "404", "410", "not found", "forbidden", "unauthorized", "authentication", "bulunamadı", "yetki", "kimlik doğrulama"]),
        new("timeout", ["timeout", "timed out", "time out", "zaman aşımı", "zaman aşım"]),
        new("connection_refused", ["refus", "reddedil", "reset", "unreachable", "not listening", "erişilemi", "erişilemez", "dinlemiyor"]),
        new("keyword_missing", ["keyword", "expected content", "expected text", "anahtar kelime", "beklenen içerik", "beklenen metin"]),
        new("host_outage", ["same host", "host-wide", "whole host", "all monitors", "other monitors", "entire server", "aynı host", "aynı sunucu", "tüm monitör", "diğer monitör", "sunucunun tamamı"]),
        new("maintenance", ["maintenance", "bakım"]),
        new("slow_dns", ["dns", "name resolution", "name server", "ad çözümle", "alan adı"]),
        new("slow_tls", ["tls", "handshake", "el sıkışma"]),
        new("slow_connect", ["tcp connect", "connect time", "network", "ağ ", "bağlantı kurma", "tcp bağlantı"]),
        new("slow_ttfb", ["ttfb", "time to first byte", "first byte", "ilk bayt", "server processing", "application", "uygulama", "backend"]),
    ];

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

    public static List<string> Classify(string? cause, bool slowdown) =>
        string.IsNullOrWhiteSpace(cause) ? [] :
        Categories.Where(c => IsSlowdownCategory(c.Name) == slowdown && c.Matches(cause.ToLowerInvariant())).Select(c => c.Name).ToList();

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
        var accepted = Labels.TryGetValue(s.Expect.PrimaryCause, out var a) ? a : [];
        r.PrimaryCauseOk = r.Categories.Count is > 0 and <= 2 && r.Categories.Any(accepted.Contains);
        if (!r.PrimaryCauseOk) r.Notes.Add($"top cause categories [{string.Join(", ", r.Categories)}], expected one of [{string.Join(", ", accepted)}]");

        // ---- evidence ----
        r.EvidenceOk = (raw.Hypotheses ?? []).Count > 0 && raw.Hypotheses!.All(h => h is not null && (h.Evidence ?? []).Any(e => !string.IsNullOrWhiteSpace(e)));
        if (!r.EvidenceOk) r.Notes.Add("a hypothesis without evidence (or none at all)");

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
