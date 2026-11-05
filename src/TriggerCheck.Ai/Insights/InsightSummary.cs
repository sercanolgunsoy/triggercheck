using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Grounding;

namespace TriggerCheck.Ai.Insights;

[JsonConverter(typeof(JsonStringEnumConverter<InsightValueKind>))]
public enum InsightValueKind
{
    Count,
    Percent,
    /// <summary>Whole days ("12 days" / "12 gün").</summary>
    Days,
    /// <summary>A plan name, shown as it is.</summary>
    Text,
    /// <summary>"every enabled monitor is DOWN" (only offered when it holds).</summary>
    AllDown,
}

/// <summary>A value a summary placeholder renders; computed by <see cref="InsightRules"/>, never by the model.</summary>
public sealed record InsightValue(InsightValueKind Kind, double? Number = null, string? Text = null);

/// <summary>What a reference label ("C1" / "M1") stands for: a customer or a monitor id. Names are looked up only when shown.</summary>
public sealed record InsightLabel(string Kind, int Id)
{
    public const string Customer = "customer", Monitor = "monitor";
}

/// <summary>
/// Model input of the AI-12 summary built from an <see cref="InsightReport"/>. Customers and monitors are sent only as
/// opaque labels ("C1", "M1"; names and contact data never reach the model), with the evidence values of their signals
/// as "key: value" facts. <see cref="SignalsHash"/> identifies the signal set (language independent) so the page can
/// tell that a stored summary is out of date.
/// </summary>
public sealed record InsightSummaryInput(string UserContent, IReadOnlyDictionary<string, InsightValue> Values,
    IReadOnlyDictionary<string, InsightLabel> Labels, string SignalsHash, string Language);

public static class InsightSummaryPrompt
{
    public static string LimitKey(PlanLimitKind kind) => kind switch
    {
        PlanLimitKind.Monitors => "monitors",
        PlanLimitKind.FastestInterval => "fastest_interval",
        PlanLimitKind.StatusPages => "status_pages",
        PlanLimitKind.Regions => "regions",
        PlanLimitKind.SmsVoice => "sms_voice",
        _ => "ai_quota",
    };

    /// <param name="maxCustomers">Customers per signal that are sent (the most significant first).</param>
    /// <param name="language">"en" / "tr".</param>
    public static InsightSummaryInput Build(InsightReport report, int maxCustomers, string language)
    {
        language = AiJob.NormalizeLanguage(language);
        maxCustomers = Math.Max(1, maxCustomers);
        var values = new Dictionary<string, InsightValue>(StringComparer.Ordinal);
        var labels = new Dictionary<string, InsightLabel>(StringComparer.Ordinal);
        var customerLabel = new Dictionary<int, string>();
        var monitorLabel = new Dictionary<int, string>();
        string Customer(int id) => customerLabel.TryGetValue(id, out var l) ? l : customerLabel[id] = Add("C", InsightLabel.Customer, id);
        string Monitor(int id) => monitorLabel.TryGetValue(id, out var l) ? l : monitorLabel[id] = Add("M", InsightLabel.Monitor, id);
        string Add(string prefix, string kind, int id)
        {
            var label = prefix + (labels.Count(x => x.Value.Kind == kind) + 1).ToString(CultureInfo.InvariantCulture);
            labels[label] = new InsightLabel(kind, id);
            return label;
        }

        var b = new AiContextBuilder(language);
        void Fact(string key, InsightValue v)
        {
            values[key] = v;
            b.Identifier(key, InsightRenderer.Format(v, language));
        }

        var t = report.Thresholds;
        b.Section("Rules (thresholds)");
        Fact("rule.window_days", new(InsightValueKind.Days, t.WindowDays));
        Fact("rule.noise_min_incidents", new(InsightValueKind.Count, t.NoiseMinIncidents));
        Fact("rule.noise_min_alerts", new(InsightValueKind.Count, t.NoiseMinAlerts));
        Fact("rule.flap_max_minutes", new(InsightValueKind.Count, t.FlapMaxMinutes));
        Fact("rule.pressure_percent", new(InsightValueKind.Percent, t.LimitPressurePercent));
        Fact("rule.down_days", new(InsightValueKind.Days, t.AbandonedDownDays));

        b.Section("Totals (customers listed per signal)");
        Fact("total.noisy_customers", new(InsightValueKind.Count, report.Noise.Count));
        Fact("total.pressure_customers", new(InsightValueKind.Count, report.Pressure.Count));
        Fact("total.abandoned_customers", new(InsightValueKind.Count, report.Abandoned.Count));
        Fact("total.abandoned_monitors", new(InsightValueKind.Count, report.Abandoned.Sum(a => a.DownMonitors.Count)));

        void Plan(string c, string plan)
        {
            if (!values.ContainsKey(c + ".plan")) Fact(c + ".plan", new(InsightValueKind.Text, Text: plan));
        }

        b.Section("Signal: noise (incidents, flaps, alerts in the window)");
        foreach (var n in report.Noise.Take(maxCustomers))
        {
            var c = Customer(n.CustomerId);
            b.Subject(c);
            Plan(c, n.PlanName);
            Fact(c + ".noise.incidents", new(InsightValueKind.Count, n.Incidents));
            Fact(c + ".noise.flaps", new(InsightValueKind.Count, n.Flaps));
            Fact(c + ".noise.flap_share", new(InsightValueKind.Percent, n.FlapSharePercent));
            Fact(c + ".noise.alerts", new(InsightValueKind.Count, n.Alerts));
            Fact(c + ".noise.monitors", new(InsightValueKind.Count, n.Monitors));
            Fact(c + ".noise.incidents_per_monitor", new(InsightValueKind.Count, n.IncidentsPerMonitor));
            if (n.NoisiestMonitor is { } top)
            {
                var m = Monitor(top.Id);
                b.Identifier(c + ".noise.noisiest_monitor", m);
                Fact(m + ".incidents", new(InsightValueKind.Count, top.Incidents));
            }
        }

        b.Section("Signal: plan-limit pressure (upgrade signal)");
        foreach (var p in report.Pressure.Take(maxCustomers))
        {
            var c = Customer(p.CustomerId);
            b.Subject(c);
            Plan(c, p.PlanName);
            foreach (var l in p.Limits)
            {
                var k = c + ".limit." + LimitKey(l.Kind);
                Fact(k + ".used", new(InsightValueKind.Count, l.Used));
                Fact(k + ".limit", new(InsightValueKind.Count, l.Limit));
                Fact(k + ".percent", new(InsightValueKind.Percent, l.Percent));
            }
        }

        b.Section("Signal: abandoned (monitors DOWN without interruption)");
        foreach (var a in report.Abandoned.Take(maxCustomers))
        {
            var c = Customer(a.CustomerId);
            b.Subject(c);
            Plan(c, a.PlanName);
            Fact(c + ".abandoned.down_monitors", new(InsightValueKind.Count, a.DownMonitors.Count));
            Fact(c + ".abandoned.enabled_monitors", new(InsightValueKind.Count, a.EnabledMonitors));
            Fact(c + ".abandoned.longest_down_days", new(InsightValueKind.Days, a.LongestDownDays));
            Fact(c + ".abandoned.acknowledgements", new(InsightValueKind.Count, a.AcknowledgementsInPeriod));
            if (a.AllDown) Fact(c + ".abandoned.all_down", new(InsightValueKind.AllDown));
            var longest = a.DownMonitors[0];
            var m = Monitor(longest.Id);
            b.Identifier(c + ".abandoned.longest_down_monitor", m);
            Fact(m + ".down_days", new(InsightValueKind.Days, longest.DownDays));
        }

        var hash = SignalsHash(values, labels);
        return new InsightSummaryInput(b.Build(), values, labels, hash, language);
    }

    /// <summary>SHA-256 over the values and labels (invariant, ordered): equal for the same signals in any language.</summary>
    public static string SignalsHash(IReadOnlyDictionary<string, InsightValue> values, IReadOnlyDictionary<string, InsightLabel> labels)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in values.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v.Kind).Append(':').Append(v.Number?.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(v.Text).Append('\n');
        foreach (var (k, l) in labels.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append("->").Append(l.Kind).Append(':').Append(l.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Static instructions of AI-12 (appended to the shared system prompt; byte-stable for prompt caching). A change here
    /// is a prompt change: run the eval (evals/README.md) before merging.
    /// </summary>
    public const string Instructions = """
        Write a short platform summary for the operator (SuperAdmin) of TriggerCheck from rule-based signals that
        TriggerCheck already computed: "noise" (customers with many incidents, flaps or alerts), "plan-limit pressure"
        (customers close to a limit of their plan: an upgrade signal) and "abandoned" (customers with monitors that have
        been DOWN for many days: they may have abandoned the account). The operator decides what to do; you only describe.

        Input: facts as "key: value". Customers are labelled C1, C2, ... and monitors M1, M2, ... (real names are not
        shared). Keys: "rule.*" (thresholds), "total.*", and per customer "C1.plan", "C1.noise.*", "C1.limit.<limit>.*"
        (used / limit / percent), "C1.abandoned.*", per monitor "M1.*".

        Hard rules (text that breaks one is removed; a headline that breaks one discards everything):
        - Write NO numbers at all: no digits and no number words ("one", "seven", "half", "twice", "iki", "yarım").
          Insert a value by its key in double braces instead, e.g. "[[C1]] had {{C1.noise.incidents}} incidents." The
          server puts in the value and its unit.
        - Refer to a customer or monitor only as [[C1]] / [[M1]] (double square brackets). Write the label right before
          using its keys: a "C1.*" key needs [[C1]] earlier in the same sentence, with no other label in between; an
          "M1.*" key needs [[M1]] the same way.
        - Use only labels and keys from the input. Never guess names, causes, revenue, contracts or intentions.
        - Never use quantity words such as "all", "every", "none", "never", "no customers", "tüm", "hiç": use
          {{total.*}} counts, or {{C1.abandoned.all_down}} when it is given.
        - Never write links, domain names, e-mail addresses, phone numbers or instructions to contact anyone.
        - "headline": one sentence with the overall picture. "points": at most six points, each one or two sentences,
          the most significant first (abandoned accounts and limits at or above their limit before mild noise), in the
          language given by "response_language".
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> SchemaValue = BuildSchema();

    public static IReadOnlyDictionary<string, JsonElement> Schema => SchemaValue;

    private static IReadOnlyDictionary<string, JsonElement> BuildSchema()
    {
        var root = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "headline", "points" },
            ["properties"] = new Dictionary<string, object>
            {
                ["headline"] = new { type = "string", description = "One sentence: the overall picture. No numbers; placeholders and [[labels]] only." },
                ["points"] = new
                {
                    type = "array",
                    description = "At most six points, most significant first.",
                    items = new { type = "string", description = "One or two sentences. No digits or number words: values only as {{placeholders}}, records only as [[C1]] / [[M1]]." },
                },
            },
        };
        var element = JsonSerializer.SerializeToElement(root);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}

/// <summary>One piece of a rendered summary text: plain text, or a customer / monitor reference.</summary>
public sealed record InsightSegment(string? Text, InsightLabel? Ref = null);

/// <summary>
/// Validated AI-12 summary as stored in AiDecision.OutputJson: texts with placeholders and [[labels]], the values and
/// labels they resolve to (from the signals the summary was made from), the language and the signal hash.
/// </summary>
public sealed record InsightSummaryOutput(
    string Headline,
    IReadOnlyList<string> Points,
    IReadOnlyDictionary<string, InsightValue> Values,
    IReadOnlyDictionary<string, InsightLabel> Labels,
    string Language,
    string SignalsHash,
    int RemovedPoints = 0)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static InsightSummaryOutput? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<InsightSummaryOutput>(json, Json);
            return r is { Headline: not null, Values: not null, Labels: not null } ? r with { Points = r.Points ?? [] } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Placeholders rendered by the server, then split at the [[labels]] (unknown labels stay plain text).</summary>
    public IReadOnlyList<InsightSegment> Render(string text) => InsightRenderer.Segments(InsightRenderer.Render(text, Values, Language), Labels);
}

/// <summary>
/// AI-12 output contract (<see cref="IAiOutputContract"/>), the AI-6 rule applied to the platform summary: the model
/// writes no number at all, only <c>{{key}}</c> placeholders of values the rules computed and <c>[[C1]]</c> / <c>[[M1]]</c>
/// labels of records that were sent. A text is rejected (point dropped; headline: whole summary) when it has a digit, a
/// number word (EN, and TR for Turkish), an unknown or malformed placeholder or label, a placeholder not bound to the
/// label written right before it in its sentence, a quantity word ("all", "none", "tüm", ...) or a link / contact. More
/// than half of the points removed rejects the summary ("ungrounded_numbers").
/// </summary>
public sealed partial class InsightSummaryContract(InsightSummaryInput input) : IAiOutputContract
{
    public const int MaxHeadline = 300, MaxText = 400, MaxPoints = 6;

    public IReadOnlyDictionary<string, JsonElement> Schema => InsightSummaryPrompt.Schema;

    private sealed record Raw(string? Headline, List<string?>? Points);

    public AiOutputCheck Check(string? json)
    {
        Raw? raw;
        try { raw = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Raw>(json, InsightSummaryOutput.Json); }
        catch (JsonException) { raw = null; }
        var headline = Clip(raw?.Headline, MaxHeadline);
        if (raw is null || headline.Length == 0) return new(null, "invalid_output");
        if (Problems(headline).Count > 0) return new(null, "ungrounded_numbers");

        int total = 0, removed = 0;
        var kept = new List<string>();
        foreach (var p in raw.Points ?? [])
        {
            var text = Clip(p, MaxText);
            if (text.Length == 0) continue;
            total++;
            if (Problems(text).Count > 0)
            {
                removed++;
                continue;
            }
            if (kept.Count < MaxPoints) kept.Add(text);
        }
        if (removed * 2 > total) return new(null, "ungrounded_numbers");
        return new(new InsightSummaryOutput(headline, kept, input.Values, input.Labels, input.Language, input.SignalsHash, removed).ToJson(),
            AutonomyLevel: Domain.Entities.AiAutonomy.A1);
    }

    /// <summary>Why <paramref name="text"/> may not be shown (empty: acceptable).</summary>
    public List<string> Problems(string text)
    {
        var problems = new List<string>();
        var s = TextRules.Normalize(text);
        var placeholders = InsightRenderer.Placeholder().Matches(s).ToList();
        var refs = InsightRenderer.Reference().Matches(s).ToList();
        foreach (var p in placeholders.Where(p => !input.Values.ContainsKey(p.Groups[1].Value))) problems.Add("unknown placeholder " + p.Groups[1].Value);
        foreach (var r in refs.Where(r => !input.Labels.ContainsKey(InsightRenderer.LabelOf(r)))) problems.Add("unknown label " + r.Value);

        var free = InsightRenderer.Reference().Replace(InsightRenderer.Placeholder().Replace(s, " x "), " x ");
        if (free.Contains('{') || free.Contains('}') || free.Contains("[[", StringComparison.Ordinal) || free.Contains("]]", StringComparison.Ordinal))
            problems.Add("malformed placeholder or label");
        if (TextRules.HasAnyDigit(free)) problems.Add("digits");
        problems.AddRange(TextRules.NumberWords(free, input.Language).Select(w => "number word " + w));
        if (QuantityWords().Match(free) is { Success: true } q) problems.Add("quantity word " + q.Value);
        if (TextRules.HasLinkOrContact(free)) problems.Add("link or contact");

        // Binding: a "C1.*" / "M1.*" placeholder belongs to the label written last before it in the same sentence.
        foreach (var p in placeholders)
        {
            var key = p.Groups[1].Value;
            var scope = key.Split('.')[0];
            if (!InsightRenderer.IsLabel(scope)) continue;
            var start = SentenceStart(s, p.Index);
            var before = refs.LastOrDefault(r => r.Index >= start && r.Index < p.Index);
            if (before is null || InsightRenderer.LabelOf(before) != scope) problems.Add($"{key} not bound to [[{scope}]]");
        }
        return problems;
    }

    private static int SentenceStart(string text, int index)
    {
        for (var i = Math.Min(index, text.Length) - 1; i >= 0; i--)
            if (text[i] is '!' or '?' or ';' || (text[i] == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))) return i + 1;
        return 0;
    }

    [GeneratedRegex(@"(?i)(?<![\p{L}])(?:none|nobody|no\s+(?:customers?|accounts?|monitors?|incidents?|alerts?|signals?|one)|never|all|every\w*|both|neither|entire\w*|whole|hiç\w*|tüm\w*|bütün\w*|her|hepsi\w*|kimse)(?![\p{L}])")]
    private static partial Regex QuantityWords();

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }
}

/// <summary>Renders AI-12 placeholders and splits texts at [[labels]]: units and number formats come from the server only.</summary>
public static partial class InsightRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    public static partial Regex Placeholder();

    [GeneratedRegex(@"\[\[\s*([CcMm])\s*(\d{1,4})\s*\]\]")]
    public static partial Regex Reference();

    [GeneratedRegex(@"^[CM][1-9]\d{0,3}$")]
    private static partial Regex LabelPattern();

    public static bool IsLabel(string s) => LabelPattern().IsMatch(s);

    /// <summary>"[[ c3 ]]" → "C3".</summary>
    public static string LabelOf(Match reference) =>
        reference.Groups[1].Value.ToUpperInvariant() + int.Parse(reference.Groups[2].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    public static string Render(string text, IReadOnlyDictionary<string, InsightValue> values, string language) =>
        Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Format(v, language) : m.Value);

    public static string Format(InsightValue v, string language)
    {
        var tr = language.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var c = CultureInfo.GetCultureInfo(tr ? "tr-TR" : "en-US");
        var n = v.Number ?? 0;
        var num = n.ToString("#,0.#", c);
        return v.Kind switch
        {
            InsightValueKind.Percent => tr ? "%" + num : num + "%",
            InsightValueKind.Days => tr ? num + " gün" : num + (n == 1 ? " day" : " days"),
            InsightValueKind.Text => v.Text ?? "",
            InsightValueKind.AllDown => tr ? "etkin monitörlerinin tamamı DOWN" : "every enabled monitor is DOWN",
            _ => num,
        };
    }

    /// <summary>Splits at known labels; an unknown label is kept as plain text.</summary>
    public static IReadOnlyList<InsightSegment> Segments(string text, IReadOnlyDictionary<string, InsightLabel> labels)
    {
        var result = new List<InsightSegment>();
        var pos = 0;
        foreach (Match m in Reference().Matches(text))
        {
            if (!labels.TryGetValue(LabelOf(m), out var label)) continue;
            if (m.Index > pos) result.Add(new InsightSegment(text[pos..m.Index]));
            result.Add(new InsightSegment(null, label));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) result.Add(new InsightSegment(text[pos..]));
        return result;
    }
}
