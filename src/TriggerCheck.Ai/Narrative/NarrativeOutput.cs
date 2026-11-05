using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Grounding;

namespace TriggerCheck.Ai.Narrative;

/// <summary>"Ai:Narrative": the AI-6 narrative report batch job and how the monthly e-mail waits for it.</summary>
public sealed class AiNarrativeOptions
{
    /// <summary>The job runs only when this, Ai:Enabled and a key are all set.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often the job looks for due / requested narratives; 0 = disabled.</summary>
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Narratives generated per run (the rest waits for the next run).</summary>
    public int MaxPerRun { get; set; } = 20;
    /// <summary>The previous month's narrative is created automatically during this many days of the new month.</summary>
    public int AutoMonthlyDays { get; set; } = 7;
    /// <summary>Attempts per period before it is given up (failed / invalid output).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>A failed attempt is retried after this many minutes.</summary>
    public int RetryMinutes { get; set; } = 60;
    /// <summary>
    /// A claim that did not finish (process crash) can be taken over after this many minutes (only while attempts are
    /// left). Must be well above one AI call (Ai:TimeoutSeconds); checked at startup.
    /// </summary>
    public int RunningTimeoutMinutes { get; set; } = 15;
    /// <summary>A failed or skipped narrative can be requested again in the panel this long after it finished.</summary>
    public int RequestCooldownMinutes { get; set; } = 60;
    /// <summary>
    /// The monthly e-mail waits for a narrative that is still being prepared at most this many minutes after the end of
    /// the month (and never past the mailer's catch-up window); then it is sent without one. It never waits when no
    /// narrative can come (AI off, opt-out, plan, budget, given up). Throughput: the job completes at most
    /// <see cref="MaxPerRun"/> narratives per <see cref="IntervalMinutes"/> (default 20 per 15 minutes, monthly ones
    /// first), so with the default wait about 240 eligible customers get the narrative in their e-mail; raise MaxPerRun
    /// (and Ai:MaxConcurrency has no effect here: calls run one after the other) or the wait for more.
    /// </summary>
    public int EmailWaitMinutes { get; set; } = 180;
}

/// <summary>One statement of a narrative section (with placeholders), with the monitor labels it is about ("monitor A").</summary>
public sealed record NarrativeItem(string Text, IReadOnlyList<string> Monitors);

/// <summary>
/// Validated AI-6 narrative as stored in AiDecision.OutputJson. Texts contain no numbers, only placeholders
/// ("{{A.uptime}}") that <see cref="NarrativeRenderer"/> fills from <see cref="Values"/> (computed from the database
/// when it was generated) in <see cref="Language"/>. <see cref="Labels"/> maps the neutral labels the model saw
/// ("monitor A") to monitor ids; names are put in only when it is shown (they never reach the model).
/// </summary>
public sealed record NarrativeOutput(
    string Headline,
    IReadOnlyList<NarrativeItem> WhatHappened,
    IReadOnlyList<NarrativeItem> WhatChanged,
    IReadOnlyList<NarrativeItem> WhatToWatch,
    IReadOnlyDictionary<string, int>? Labels = null,
    int RemovedItems = 0,
    IReadOnlyDictionary<string, NarrativeValue>? Values = null,
    string Language = "en")
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static NarrativeOutput? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<NarrativeOutput>(json, Json);
            return r is { Headline: not null } ? r with
            {
                WhatHappened = r.WhatHappened ?? [],
                WhatChanged = r.WhatChanged ?? [],
                WhatToWatch = r.WhatToWatch ?? []
            } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A text of this narrative ready to show: placeholders rendered, labels replaced with monitor names.</summary>
    public string Render(string text, IReadOnlyDictionary<string, string>? names) =>
        NarrativeReports.WithNames(NarrativeRenderer.Render(text, Values, Language), names);
}

/// <summary>
/// Output contract of AI-6 (<see cref="IAiOutputContract"/>). The "LLM produces no numbers" rule is structural: the
/// model's text may contain NO number at all, only placeholders for the facts the server computed (<paramref name="values"/>)
/// and renders itself. Each text is rejected (item dropped; headline: whole narrative) when it has:
/// <list type="bullet">
/// <item>a digit of any script, a spelled-out number, fraction, ordinal or multiplier (EN; TR for Turkish narratives;
/// "bir" is allowed as the Turkish article, English "one" is not);</item>
/// <item>an unknown or malformed placeholder;</item>
/// <item>an SLA verdict in its own words ("met", "missed", "kept its SLA", "karşıladı", ...) or a quantity in words
/// ("no incidents", "all monitors", "perfect", "hiç"): the result is only shown through {{X.sla_status}} and counts;</item>
/// <item>a monitor placeholder not bound to the monitor named right before it ("monitor A had {{B.uptime}}"), a
/// customer-wide value in a sentence about a monitor without "overall / total", a previous-period value without a
/// previous-period word, or a direction word next to a change placeholder (the change renders its own direction);</item>
/// <item>a link, domain (also obfuscated), e-mail address or phone number.</item>
/// </list>
/// More than half of the items removed rejects the narrative ("ungrounded_numbers"); monitor references must be labels
/// that were sent; lengths and counts are capped.
/// </summary>
/// <param name="language">"en" / "tr": the narrative's language (number words, rendering).</param>
public sealed partial class NarrativeOutputContract(IReadOnlyDictionary<string, NarrativeValue> values, IReadOnlyDictionary<string, int> labels,
    string language = "en") : IAiOutputContract
{
    public const int MaxHeadline = 300, MaxText = 400, MaxItems = 6;

    private static readonly IReadOnlyDictionary<string, JsonElement> SchemaValue = BuildSchema();

    public IReadOnlyDictionary<string, JsonElement> Schema => SchemaValue;

    public IReadOnlyDictionary<string, NarrativeValue> Values => values;

    /// <summary>Label → monitor id of the monitors that were sent.</summary>
    public IReadOnlyDictionary<string, int> Labels => labels;

    public string Language => language;

    private sealed record Raw(string? Headline, List<RawItem?>? WhatHappened, List<RawItem?>? WhatChanged, List<RawItem?>? WhatToWatch);

    private sealed record RawItem(string? Text, List<string?>? Monitors);

    public AiOutputCheck Check(string? json)
    {
        Raw? raw;
        try { raw = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Raw>(json, NarrativeOutput.Json); }
        catch (JsonException) { raw = null; }
        var headline = Clip(raw?.Headline, MaxHeadline);
        if (raw is null || headline.Length == 0) return new(null, "invalid_output");
        if (Problems(headline).Count > 0) return new(null, "ungrounded_numbers");

        int total = 0, removed = 0;
        List<NarrativeItem> Section(List<RawItem?>? items)
        {
            var kept = new List<NarrativeItem>();
            foreach (var i in items ?? [])
            {
                var text = Clip(i?.Text, MaxText);
                if (text.Length == 0) continue;
                total++;
                if (Problems(text).Count > 0)
                {
                    removed++;
                    continue;
                }
                if (kept.Count >= MaxItems) continue;
                var refs = (i!.Monitors ?? []).Select(NormalizeLabel).OfType<string>().Where(labels.ContainsKey).Distinct().Take(MaxItems).ToList();
                kept.Add(new NarrativeItem(text, refs));
            }
            return kept;
        }
        var happened = Section(raw.WhatHappened);
        var changed = Section(raw.WhatChanged);
        var watch = Section(raw.WhatToWatch);
        if (removed * 2 > total) return new(null, "ungrounded_numbers");
        return new(new NarrativeOutput(headline, happened, changed, watch, labels, removed, values, language).ToJson());
    }

    /// <summary>Why <paramref name="text"/> may not be shown (empty: acceptable). See the class summary.</summary>
    public List<string> Problems(string text)
    {
        var problems = new List<string>();
        var normalized = TextRules.Normalize(text);
        var placeholders = NarrativeRenderer.Placeholder().Matches(normalized).ToList();
        foreach (var p in placeholders.Where(p => !values.ContainsKey(p.Groups[1].Value)))
            problems.Add("unknown placeholder " + p.Groups[1].Value);
        // Free text: placeholders replaced by a neutral word of the same length (positions stay comparable).
        var free = NarrativeRenderer.Placeholder().Replace(normalized, m => " " + new string('x', Math.Max(0, m.Length - 2)) + " ");
        if (free.Contains('{') || free.Contains('}')) problems.Add("malformed placeholder");
        if (TextRules.HasAnyDigit(free)) problems.Add("digits");
        problems.AddRange(TextRules.NumberWords(free, language).Select(w => "number word " + w));
        if (SlaVerdict().Match(free) is { Success: true } verdict) problems.Add("sla verdict " + verdict.Value);
        if (SlaContext().IsMatch(free) && SlaAdjacent().Match(free) is { Success: true } adj) problems.Add("sla verdict " + adj.Value);
        if (QuantityWords().Match(free) is { Success: true } q) problems.Add("quantity word " + q.Value);
        if (TextRules.HasLinkOrContact(free)) problems.Add("link or contact");

        // Binding: each monitor placeholder belongs to the monitor(s) named last before it.
        var mentions = Mentions(normalized);
        foreach (var unknown in mentions.SelectMany(m => m.Labels).Where(l => !labels.ContainsKey(l)).Distinct())
            problems.Add("unknown " + unknown);
        foreach (var p in placeholders)
        {
            var key = p.Groups[1].Value;
            var scope = key.Split('.')[0];
            var sentence = SentenceOf(normalized, p.Index);
            var sentenceFree = NarrativeRenderer.Placeholder().Replace(sentence.Text, " ");
            var sentenceMentions = mentions.Where(m => m.Index >= sentence.Start && m.Index < sentence.End).ToList();
            if (scope.Length == 1 && char.IsAsciiLetterUpper(scope[0]))
            {
                var before = mentions.Where(m => m.Index < p.Index).ToList();
                if (before.Count == 0 || !before[^1].Labels.Contains("monitor " + scope)) problems.Add($"{key} not bound to monitor {scope}");
            }
            else if (scope is "total" or "prev" or "change" or "others" && sentenceMentions.Count > 0 && !TotalCue().IsMatch(sentenceFree))
                problems.Add($"{key} (customer-wide) in a sentence about a monitor");
            if ((scope == "prev" || key.Contains(".prev.", StringComparison.Ordinal))
                && !PreviousCue().IsMatch(sentenceFree)
                && !NarrativeRenderer.Placeholder().Matches(sentence.Text).Any(x => x.Groups[1].Value == "period.previous_name"))
                problems.Add($"{key} (previous period) without a previous-period word");
            if (scope == "change" && DirectionWord().Match(sentenceFree) is { Success: true } dir)
                problems.Add($"direction word \"{dir.Value}\" next to {key}");
        }
        return problems;
    }

    /// <summary>Monitor labels named in a text, in order: "monitor A", "Monitor A", "MONITOR A", "monitor-A", "monitors A and B", "A monitörü".</summary>
    public static List<(int Index, List<string> Labels)> Mentions(string text)
    {
        var result = new List<(int Index, List<string> Labels)>();
        foreach (Match m in LabelList().Matches(text))
            result.Add((m.Index, Regex.Matches(m.Groups[1].Value, "[A-Z]").Select(x => "monitor " + x.Value).ToList()));
        foreach (Match m in LabelBefore().Matches(text))
            result.Add((m.Index, ["monitor " + m.Groups[1].Value]));
        return result.OrderBy(r => r.Index).ToList();
    }

    [GeneratedRegex(@"(?i:\bmonit(?:o|ö)r(?:s|ler|lerden)?)[\s-]+([A-Z](?:\s*(?:,|and|ve|or|veya|&)\s*[A-Z])*)(?![\p{L}\p{N}])")]
    private static partial Regex LabelList();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])([A-Z])\s+(?i:monit(?:ö|o)r)\p{L}*")]
    private static partial Regex LabelBefore();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(?:met|meet|meets|meeting|missed|miss|misses|missing|breach\w*|violat\w*|achiev\w*|fulfil\w*|satisf\w*|compliant|compliance|attain\w*|exceed\w*|fell short|on target|off target|on track|karşıla(?:dı|madı|ndı|nmadı|yamadı|yor|mıyor|nıyor|nmıyor)|kaçır\w*|tuttur\w*|tuttu|tutamadı|tutulamadı|tutuldu|sağladı|sağlayamadı|sağlanmadı|sağlandı|ulaştı|ulaşamadı|ulaşıldı|ulaşılamadı|aştı|aşamadı|aşıldı|ihlal\w*|başar\w*)(?![\p{L}])")]
    private static partial Regex SlaVerdict();

    [GeneratedRegex(@"(?i)\b(?:sla|target|objective|goal)s?\b|hedef\p{L}*")]
    private static partial Regex SlaContext();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(?:kept|keeps|keep|hit|hits|reached|reaches|made|makes|failed|fails|stayed|stays|remained|remains|above|below|under|over|beat|beats|within|altında|üzerinde|gerisinde|içinde)(?![\p{L}])")]
    private static partial Regex SlaAdjacent();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(?:none|no\s+(?:incidents?|downtime|outages?|failures?|errors?|problems?|issues?|monitors?)|without\s+(?:any\s+)?(?:incidents?|downtime|outages?|interruptions?)|never|all|every|both|neither|perfect\w*|flawless\w*|spotless|uninterrupted|hiç\w*|tüm|bütün|her|kesintisiz|sorunsuz|kusursuz|mükemmel)(?![\p{L}])")]
    private static partial Regex QuantityWords();

    [GeneratedRegex(@"(?i)\b(total|overall|in total|across|combined|altogether|customer-wide|toplam\w*|genel)\b")]
    private static partial Regex TotalCue();

    [GeneratedRegex(@"(?i)\b(previous|prior|last (?:month|week|period)|earlier|before|önceki|geçen|öncesinde)\b")]
    private static partial Regex PreviousCue();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(rose|risen|rise|increased?|increasing|grew|grow|grown|up|climbed|jumped|fell|fallen|fall|dropped|drop|decreased?|declined?|down|shrank|reduced|cut|lowered|improved?|worsened?|higher|lower|more|fewer|less|better|worse|arttı|artış|azaldı|azalış|düştü|düşüş|yükseldi|geriledi|iyileşti|kötüleşti|daha)(?![\p{L}])")]
    private static partial Regex DirectionWord();

    private static (int Start, int End, string Text) SentenceOf(string text, int index)
    {
        bool IsEnd(int i) => text[i] is '!' or '?' or ';' || (text[i] == '.' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])));
        var start = 0;
        for (var i = Math.Min(index, text.Length) - 1; i >= 0; i--)
            if (IsEnd(i)) { start = i + 1; break; }
        var end = text.Length;
        for (var i = index; i < text.Length; i++)
            if (IsEnd(i)) { end = i; break; }
        return (start, end, text[start..end]);
    }

    [GeneratedRegex(@"^(?:monit(?:o|ö)r\s+)?([A-Za-z])$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRef();

    /// <summary>"monitor a" / "Monitör A" / "A" → "monitor A"; null when it is not a label.</summary>
    public static string? NormalizeLabel(string? reference)
    {
        var m = LabelRef().Match((reference ?? "").Trim());
        return m.Success ? "monitor " + m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>The neutral label of the i-th listed monitor: "monitor A" .. "monitor Z".</summary>
    public static string Label(int index) => "monitor " + (char)('A' + index);

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildSchema()
    {
        object Items(string description) => new
        {
            type = "array",
            description,
            items = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "text", "monitors" },
                properties = new Dictionary<string, object>
                {
                    ["text"] = new { type = "string", description = "One or two sentences. No digits or number words: facts only as {{placeholders}} from the input." },
                    ["monitors"] = new { type = "array", items = new { type = "string" }, description = "Labels of the monitors the statement is about, e.g. \"monitor A\"." },
                },
            },
        };
        var root = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "headline", "what_happened", "what_changed", "what_to_watch" },
            ["properties"] = new Dictionary<string, object>
            {
                ["headline"] = new { type = "string", description = "One sentence: the overall picture of the period. No numbers; placeholders only." },
                ["what_happened"] = Items("The most important events of the period (incidents, SLA results)."),
                ["what_changed"] = Items("Changes compared with the previous period."),
                ["what_to_watch"] = Items("Risks and things to keep an eye on next period."),
            },
        };
        var element = JsonSerializer.SerializeToElement(root);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}

/// <summary>
/// Static instructions of AI-6 (appended to the shared system prompt; byte-stable for prompt caching). A change here is
/// a prompt change: run the eval (evals/README.md) before merging.
/// </summary>
public static class NarrativePrompts
{
    public const string Instructions = """
        Write a short narrative for an uptime / SLA report of one customer: what happened in the period, what changed
        compared with the previous period, and what to watch next.

        Input: a list of facts, each with a key and its value as it will be shown, e.g. "A.uptime: 99.814%". Keys:
        "period.*" (the period), "total.*" (all monitors of this period), "prev.*" (all monitors, previous period),
        "change.*" (this minus previous; the shown value already says more / fewer, higher / lower), and per monitor
        "A.*", "B.*", ... for "monitor A", "monitor B" (real names are not shared), including "A.prev.*" for the previous
        period and "A.sla_status" (met / missed). All values were computed by TriggerCheck from its database.

        Hard rules (text that breaks one is removed; a headline that breaks one discards everything):
        - Write NO numbers at all: no digits and no number words ("one", "seven", "half", "twice", "zero", "iki", "yarım").
          Insert a fact by its key in double braces instead, e.g. "monitor A was down for {{A.downtime}} in
          {{A.incidents}} incidents." The server puts in the value, the unit and the right number format.
        - Never state an SLA result in your own words (not "met", "missed", "kept", "hit", "achieved", "karşıladı",
          "kaçırdı"). Use the status placeholder: "SLA result of monitor A ({{A.sla_target}} target): {{A.sla_status}}."
        - Never use quantity words such as "no incidents", "all", "every", "none", "never", "perfect", "hiç", "tüm".
        - Write "monitor A" right before using any "A.*" key, and only A's keys after naming monitor A. In a sentence
          about a monitor, customer-wide keys ("total.*", "prev.*", "change.*") need the word "overall" / "toplam".
        - Previous-period keys ("prev.*", "A.prev.*") need "previous period" / "önceki dönem" or {{period.previous_name}}
          in the same sentence.
        - Change keys ("change.*") already say the direction: do not add "rose", "fell", "more", "less", "arttı" etc.
          Write e.g. "Incidents compared with {{period.previous_name}}: {{change.incidents}}."
        - Refer to monitors only by their labels ("monitor A") and list the labels in "monitors".
        - Never write links, domain names, e-mail addresses, phone numbers or instructions to contact anyone.
        - Missed SLA targets, the longest outages and clear changes come first. "what_to_watch": risks visible in the
          data (SLA status, slowdowns, certificate expiry). At most 4 items per section, each one or two sentences, in
          the language given by "response_language".
        """;
}
