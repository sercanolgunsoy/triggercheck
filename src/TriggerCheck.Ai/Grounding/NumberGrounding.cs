using System.Globalization;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Interpretation;

namespace TriggerCheck.Ai.Grounding;

/// <summary>How strictly <see cref="NumberGrounding"/> matches written numbers against the facts.</summary>
/// <param name="AllowRounding">
/// A value written with d decimals matches a fact that rounds to it, within <paramref name="MaxRelativeRoundingError"/>
/// and never across a known threshold (an SLA target of the subjects).
/// </param>
/// <param name="ConvertTimeUnits">"1 h 15 min" matches 75 minutes, "1.2 s" matches 1200 ms. Off: units must agree.</param>
/// <param name="GuardZeroAndHundred">A written 0 or 100 needs a fact that is exactly that value (no rounding to it).</param>
/// <param name="RejectLinksAndContacts">URLs, domains (also obfuscated), e-mail addresses and phone numbers are ungrounded.</param>
/// <param name="RejectNumberWords">Spelled-out numbers and multipliers ("seven", "twice", "yarım", "x75") are ungrounded.</param>
/// <param name="PreviousPeriodCues">Extra words that refer to the previous period (its month names, key).</param>
public sealed record GroundingPolicy(
    bool AllowRounding = false,
    bool ConvertTimeUnits = false,
    bool GuardZeroAndHundred = true,
    bool RejectLinksAndContacts = false,
    bool RejectNumberWords = true,
    decimal MaxRelativeRoundingError = 0.05m,
    IReadOnlyList<string>? PreviousPeriodCues = null)
{
    /// <summary>Numbers exactly as given, units as given, no number words (AI-5).</summary>
    public static readonly GroundingPolicy Strict = new();
}

/// <summary>
/// Shared "numbers come from the database, never from the model" engine (AI-5; AI-7 later). The AI-6 narrative does not
/// use it: its text may contain no numbers at all (placeholders, <see cref="Narrative.NarrativeOutputContract"/>).
/// Every number in a model text must be backed by a typed fact of the input (<see cref="GroundingFacts"/>):
/// <list type="bullet">
/// <item>dates and times as whole values ("2026-03-10", "10.03.2026", "10 Mart", "March 10", "February 2026", "11:58";
/// "1:58" never matches "11:58"); IP addresses verbatim; HTTP status codes only after "HTTP / status / code";</item>
/// <item>one reading per response language: English "1,234.5" (comma groups thousands), Turkish "1.234,5"; a separator
/// is a thousands separator only in "1,234" / "1.234" shapes; ASCII digits only, anything unparseable is ungrounded;</item>
/// <item>units must agree (a number without a unit matches only plain / count facts); time units convert only when the
/// policy allows; a negative fact needs a written minus or "ago / expired";</item>
/// <item>context: a stated change ("rose / fell / was cut by", "3 more / fewer", "arttı / azaldı") needs a delta with that
/// direction, other deltas need a change word; previous-period values need a previous-period word; with subjects
/// ("monitor A"), customer-wide values of the same metric need "total / overall"; a metric named next to the number
/// ("41 incidents", "p95 is 10 s") must be the fact's metric;</item>
/// <item>spelled-out numbers and multipliers are rejected (policy).</item>
/// </list>
/// </summary>
public static partial class NumberGrounding
{
    // Names with digits that are not quantities: p95, 5xx, HTTP/1.1 / HTTP/2, TLS 1.3, IPv4, 24/7.
    [GeneratedRegex(@"(?i)\bp(?:50|75|90|95|99)\b|\b[1-5]xx\b|\bhttps?/(?:1\.[01]|2|3)\b|\btls ?v?1\.[0-3]\b|\bipv[46]\b|\b24/7\b")]
    private static partial Regex NonQuantity();

    [GeneratedRegex(@"(?<![0-9])([0-9]{4})-([0-9]{2})-([0-9]{2})(?:[T ]([0-9]{2}):([0-9]{2})(?::[0-9]{2})?Z?)?(?![0-9])")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<![0-9-])([0-9]{4})-([0-9]{2})(?![0-9-])")]
    private static partial Regex IsoMonth();

    [GeneratedRegex(@"(?<![0-9.,/])([0-9]{1,2})([./-])([0-9]{1,2})\2([0-9]{4})(?![0-9])")]
    private static partial Regex NumericDate();

    private const string MonthNames =
        "january|february|march|april|may|june|july|august|september|october|november|december|" +
        "jan|feb|mar|apr|jun|jul|aug|sep|sept|oct|nov|dec|" +
        "ocak|şubat|mart|nisan|mayıs|haziran|temmuz|ağustos|eylül|ekim|kasım|aralık";

    [GeneratedRegex(@"(?i)(?<![0-9])([0-9]{1,2})\.?\s+(" + MonthNames + @")(?![\p{L}])(?:\s+([0-9]{4}))?")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(" + MonthNames + @")\.?\s+([0-9]{1,2})(?:st|nd|rd|th)?(?![0-9])(?:,?\s+([0-9]{4}))?")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(" + MonthNames + @")(?:'[\p{L}]+)?\s+([0-9]{4})(?![0-9])")]
    private static partial Regex MonthYear();

    [GeneratedRegex(@"(?<![0-9:.,])([0-9]{1,2}):([0-9]{2})(?::[0-9]{2})?(?![0-9:])")]
    private static partial Regex Time();

    [GeneratedRegex(@"(?<![0-9.])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?![0-9.])")]
    private static partial Regex Ip();

    [GeneratedRegex(@"(?i)\b(?:https?|status(?: code)?|code|response|durum kodu|kod(?:u)?)\s*[:=]?\s*([1-5][0-9]{2})(?![0-9])|(?<![\p{L}\p{N}.,])([1-5][0-9]{2})(?=\s+(?:not found|forbidden|unauthori[sz]ed|bad gateway|service unavailable|internal server error|gateway timeout|bad request|too many requests|errors?|responses?|status)\b)")]
    private static partial Regex StatusCode();

    /// <summary>A bare year (1990-2100) that is not a quantity with a unit ("2026", not "2026 ms").</summary>
    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}.,])(?:199[0-9]|20[0-9]{2}|2100)(?![0-9.,:\-/])(?!\s?(?:%|ms|s|secs?|seconds?|mins?|minutes?|h|hrs?|hours?|d|days?|dk|sn|sa|saat|gün|dakika|saniye|incidents?|checks?)\b)")]
    private static partial Regex BareYear();

    private const string TimeUnit =
        @"milliseconds?|milisaniye\p{L}*|ms|seconds?|secs?|saniye\p{L}*|sn|minutes?|mins?|dakika\p{L}*|dk|hours?|hrs?|saat\p{L}*|sa|days?|gün\p{L}*|s|m|h|d";

    // Two or more "<number> <time unit>" parts: "2 h 15 min", "1 gün 3 saat".
    [GeneratedRegex(@"(?i)(?<![\p{L}0-9.,])([0-9]+(?:[.,][0-9]+)?)[\s-]?(" + TimeUnit + @")(?:'\p{L}+)?(?![\p{L}])(?:(?:\s*,\s*|\s+(?:and|ve)\s+|\s+)([0-9]+(?:[.,][0-9]+)?)[\s-]?(" + TimeUnit + @")(?:'\p{L}+)?(?![\p{L}]))+")]
    private static partial Regex MultiDuration();

    // A number (not glued to a preceding letter: "W05", "E1" are ids), with an optional sign, "%" before it and unit after it.
    [GeneratedRegex(@"(?i)(?<![\p{L}0-9.,])([-−])?(%\s?)?([0-9]+(?:[.,][0-9]+)*)(?![0-9])(?:[\s-]?(%|percentage points?|points?|puan|pp|yüzde puan|" + TimeUnit + @")(?:'\p{L}+)?(?![\p{L}]))?")]
    private static partial Regex Quantity();

    [GeneratedRegex(@"(?i)\b(rose|risen|increased|grew|grown|went up|climbed|jumped|raised|lengthened|fell|fallen|dropped|decreased|declined|went down|shrank|shrunk|reduced|cut|lowered|shortened|improved|worsened|deteriorated|changed)\s+(?:by\s+)?(?:about\s+|around\s+|nearly\s+|almost\s+|roughly\s+)?$")]
    private static partial Regex DirectionBefore();

    [GeneratedRegex(@"(?i)^\s*(?:(?!or\b|veya\b|ya\b)(?:\p{L}+)\s+)?(more|higher|longer|slower|fewer|less|lower|shorter|faster|arttı|artış gösterdi|yükseldi|azaldı|düştü|geriledi|daha fazla|daha az)\b")]
    private static partial Regex DirectionAfter();

    [GeneratedRegex(@"(?i)\b(change[sd]?|difference|compared|than|versus|vs\.?|up|down|improv\w*|worsen\w*|fark\w*|göre|değiş\w*|arttı|azaldı|düştü|yükseldi|geriledi)\b")]
    private static partial Regex ChangeCue();

    [GeneratedRegex(@"(?i)\b(previous|prior|last (?:month|week|period)|earlier|before|a (?:month|week) ago|önceki|geçen|öncesinde)\b")]
    private static partial Regex PreviousCue();

    [GeneratedRegex(@"(?i)\b(total|overall|in total|across|combined|altogether|all monitors|customer-wide|toplam\w*|genel|tüm monitör\w*)\b")]
    private static partial Regex TotalCue();

    [GeneratedRegex(@"(?i)\b(ago|expired|overdue|önce|doldu|sona erdi|geçti)\b")]
    private static partial Regex PastCue();

    [GeneratedRegex(@"[\p{L}\p{N}_#./:-]+")]
    private static partial Regex Word();

    /// <summary>The parts of <paramref name="text"/> with a number, date or time that the facts do not back.</summary>
    /// <param name="subjects">Entities the text is about ("monitor A"); null / empty = all facts count.</param>
    public static List<string> Ungrounded(string? text, GroundingFacts facts, GroundingPolicy policy, IReadOnlyCollection<string>? subjects = null)
    {
        var bad = new List<string>();
        if (string.IsNullOrEmpty(text)) return bad;
        var s0 = TextRules.Normalize(text);
        if (policy.RejectLinksAndContacts && TextRules.HasLinkOrContact(s0)) bad.Add("link or contact");
        if (s0.Any(c => !char.IsAsciiDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherNumber or UnicodeCategory.LetterNumber))
            bad.Add("non-ASCII digits");
        if (policy.RejectNumberWords) bad.AddRange(TextRules.NumberWords(s0, facts.Language));

        var s = NonQuantity().Replace(s0, m => new string(' ', m.Length));
        var used = new bool[s.Length];
        bool Free(Match m) => !used.AsSpan(m.Index, m.Length).Contains(true);
        void Use(Match m) => used.AsSpan(m.Index, m.Length).Fill(true);
        void Check(Regex rx, Func<Match, bool> ok)
        {
            foreach (Match m in rx.Matches(s))
            {
                if (!Free(m)) continue;
                Use(m);
                if (!ok(m)) bad.Add(m.Value.Trim());
            }
        }

        Check(IsoDate(), m => DateKnown(facts, Int(m, 1), Int(m, 2), Int(m, 3))
                              && (!m.Groups[4].Success || facts.Times.Contains((Int(m, 4), Int(m, 5)))));
        Check(NumericDate(), m => DateKnown(facts, Int(m, 4), Int(m, 3), Int(m, 1))
                                  || (m.Groups[2].Value == "/" && DateKnown(facts, Int(m, 4), Int(m, 1), Int(m, 3))));
        Check(IsoMonth(), m => facts.Months.Contains((Int(m, 1), Int(m, 2))));
        Check(DayMonth(), m => DayKnown(facts, Int(m, 1), MonthOf(m.Groups[2].Value), m.Groups[3].Success ? Int(m, 3) : null));
        Check(MonthDay(), m => DayKnown(facts, Int(m, 2), MonthOf(m.Groups[1].Value), m.Groups[3].Success ? Int(m, 3) : null));
        Check(MonthYear(), m => facts.Months.Contains((Int(m, 2), MonthOf(m.Groups[1].Value))));
        Check(Time(), m => facts.Times.Contains((Int(m, 1), Int(m, 2))));
        Check(Ip(), m => facts.Literals.Contains(m.Value));
        Check(StatusCode(), m => facts.StatusCodes.Contains(m.Groups[1].Success ? Int(m, 1) : Int(m, 2)));
        foreach (Match m in BareYear().Matches(s))
        {
            // A year of a known date passes; otherwise it is left to the number check below (e.g. a count of 2026).
            if (!Free(m) || !int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y) || !facts.Months.Any(x => x.Year == y)) continue;
            Use(m);
        }

        foreach (Match m in MultiDuration().Matches(s))
        {
            if (!Free(m) || !policy.ConvertTimeUnits) continue; // without conversion: checked part by part below
            Use(m);
            var parts = m.Groups[1].Captures.Concat(m.Groups[3].Captures).Zip(m.Groups[2].Captures.Concat(m.Groups[4].Captures)).ToList();
            decimal total = 0, smallest = decimal.MaxValue; // milliseconds
            var ok = true;
            foreach (var (num, unit) in parts)
            {
                var values = Parse(num.Value, facts.Language);
                if (values.Count == 0 || MsPer(UnitOf(unit.Value)) is not decimal per) { ok = false; break; }
                total += values[0].Number * per;
                smallest = Math.Min(smallest, per);
            }
            var ctx = Cues(s0, m, policy);
            var tolerance = policy.AllowRounding ? smallest / 2 : 0;
            if (!ok || !Candidates(facts, subjects, ctx, negative: false).Any(f => !f.IsDelta && MsPer(f.Unit) is decimal fp
                    && Math.Abs(f.Value * fp - total) <= tolerance && (total == f.Value * fp || RelativeOk(f.Value * fp, total, policy))))
                bad.Add(m.Value.Trim());
        }

        foreach (Match m in Quantity().Matches(s))
        {
            if (!Free(m)) continue;
            Use(m);
            var negative = m.Groups[1].Success;
            var unitText = m.Groups[2].Success ? "%" : m.Groups[4].Value;
            var unit = unitText.Length == 0 ? GroundingUnit.None : UnitOf(unitText);
            var values = Parse(m.Groups[3].Value, facts.Language);
            var ctx = Cues(s0, m, policy);
            var candidates = Candidates(facts, subjects, ctx, negative).ToList();
            var thresholds = facts.For(subjects).Where(f => f.Key.EndsWith("_target", StringComparison.OrdinalIgnoreCase)).Select(f => f.Value).ToList();
            if (values.Count == 0 || !values.Any(v => candidates.Any(f => Matches(v, unit, f, policy, thresholds))))
                bad.Add(m.Value.Trim());
        }
        return bad;
    }

    /// <summary>
    /// Adds the numbers, dates, times and months written in an earlier text of the same conversation (a question, an
    /// earlier answer) as facts with the unit they were written with, so a follow-up that repeats them is not flagged.
    /// Only for conversational context (AI-7); model input facts are recorded by the context builder.
    /// </summary>
    public const string ConversationKey = "conversation";

    public static void AddFactsFromText(GroundingFacts facts, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var s = NonQuantity().Replace(TextRules.Normalize(text), m => new string(' ', m.Length));
        foreach (Match m in IsoDate().Matches(s))
            if (Int(m, 2) is >= 1 and <= 12 && Int(m, 3) is >= 1 and <= 31 && Int(m, 3) <= DateTime.DaysInMonth(Math.Max(1, Int(m, 1)), Int(m, 2)))
                facts.AddDate(new DateTime(Int(m, 1), Int(m, 2), Int(m, 3), m.Groups[4].Success ? Int(m, 4) : 0, m.Groups[5].Success ? Int(m, 5) : 0, 0), m.Groups[4].Success);
        foreach (Match m in Time().Matches(s))
            if (Int(m, 1) is >= 0 and < 24 && Int(m, 2) is >= 0 and < 60) facts.AddTime(Int(m, 1), Int(m, 2));
        foreach (Match m in MonthYear().Matches(s))
            if (MonthOf(m.Groups[1].Value) > 0) facts.AddMonth(Int(m, 2), MonthOf(m.Groups[1].Value));
        var rest = IsoDate().Replace(s, x => new string(' ', x.Length));
        rest = Time().Replace(rest, x => new string(' ', x.Length));
        foreach (Match m in Quantity().Matches(rest))
        {
            var unitText = m.Groups[2].Success ? "%" : m.Groups[4].Value;
            foreach (var v in Parse(m.Groups[3].Value, facts.Language))
                facts.AddNumber(ConversationKey, v.Number, unitText.Length == 0 ? GroundingUnit.None : UnitOf(unitText));
        }
    }

    public static bool IsGrounded(string? text, GroundingFacts facts, GroundingPolicy policy, IReadOnlyCollection<string>? subjects = null) =>
        Ungrounded(text, facts, policy, subjects).Count == 0;

    /// <summary>URL, domain name (also obfuscated), e-mail address or phone number in <paramref name="text"/>.</summary>
    public static bool HasLinkOrContact(string text) => TextRules.HasLinkOrContact(text);

    /// <summary>
    /// Interpretation features (AI-5): removes every hypothesis, evidence line, action and unknown with an ungrounded
    /// number; null (unusable) when the summary has one.
    /// </summary>
    public static AiInterpretation? Apply(AiInterpretation raw, GroundingFacts facts, GroundingPolicy policy)
    {
        bool Ok(string? t) => IsGrounded(t, facts, policy);
        if (!Ok(raw.Summary)) return null;
        var hypotheses = (raw.Hypotheses ?? [])
            .Where(h => h is not null && Ok(h.Cause))
            .Select(h => h with { Evidence = (h.Evidence ?? []).Where(Ok).ToList() })
            .Where(h => h.Evidence.Count > 0)
            .ToList();
        return raw with
        {
            Hypotheses = hypotheses,
            SuggestedActions = (raw.SuggestedActions ?? []).Where(a => a is not null && Ok(a.Description)).ToList(),
            Unknowns = (raw.Unknowns ?? []).Where(Ok).ToList(),
        };
    }

    /// <summary>A written number and its decimals.</summary>
    public readonly record struct Value(decimal Number, int Decimals);

    /// <summary>
    /// The reading of a digit token in the response language: English "1,234.5" (comma = thousands only in groups of
    /// three, dot = decimal), Turkish "1.234,5". Empty when it does not parse (overflow, "1.2.3", the other language's
    /// shape such as English "4.500" for 4500 is read as 4.5).
    /// </summary>
    public static List<Value> Parse(string token, string language = "en")
    {
        var result = new List<Value>();
        if (token.Length == 0 || token.Any(c => c is not (>= '0' and <= '9' or '.' or ',')) || !char.IsAsciiDigit(token[0]) || !char.IsAsciiDigit(token[^1]))
            return result;
        var turkish = language.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var dec = turkish ? ',' : '.';
        var grp = turkish ? '.' : ',';
        if (token.Count(c => c == dec) > 1) return result;
        var cut = token.IndexOf(dec);
        var intPart = cut < 0 ? token : token[..cut];
        var fraction = cut < 0 ? "" : token[(cut + 1)..];
        if (fraction.Contains(grp)) return result;
        var groups = intPart.Split(grp);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3))) return result;
        var digits = string.Concat(groups);
        if (decimal.TryParse(fraction.Length == 0 ? digits : digits + "." + fraction, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
            result.Add(new Value(v, fraction.Length));
        return result;
    }

    /// <summary>Sentence-level context of one written number.</summary>
    private sealed record Context(int Direction, bool ChangeCue, bool PreviousCue, bool TotalCue, bool PastCue, string[]? Metric, int GoodChange,
        bool MetricBefore);

    private static Context Cues(string s0, Match m, GroundingPolicy policy)
    {
        var sentence = Sentence(s0, m.Index);
        var start = Math.Max(0, m.Index - 50);
        var before = s0[start..m.Index];
        var cut = before.LastIndexOfAny([';', ',', '!', '?', ':', '(']);
        if (cut >= 0) before = before[(cut + 1)..];
        var after = s0[Math.Min(s0.Length, m.Index + m.Length)..];

        int direction = 0, good = 0;
        var b = DirectionBefore().Match(before);
        var word = b.Success ? b.Groups[1].Value.ToLowerInvariant() : DirectionAfter().Match(after) is { Success: true } a ? a.Groups[1].Value.ToLowerInvariant() : null;
        switch (word)
        {
            case null: break;
            case "improved": good = 1; break;
            case "worsened" or "deteriorated": good = -1; break;
            case "changed": break;
            case "rose" or "risen" or "increased" or "grew" or "grown" or "went up" or "climbed" or "jumped" or "raised" or "lengthened"
                or "more" or "higher" or "longer" or "slower" or "arttı" or "artış gösterdi" or "yükseldi" or "daha fazla":
                direction = 1; break;
            default: direction = -1; break;
        }
        var lower = sentence.ToLowerInvariant();
        // "from 100% to 99.8%" / "3'ten 7'ye": the number right after "from" (or with an ablative suffix) is the earlier value.
        var fromValue = Regex.IsMatch(before, @"(?i)\bfrom\s*$") || Regex.IsMatch(after, @"^'?(?:t|d)[ae]n\b");
        var previous = fromValue || PreviousCue().IsMatch(sentence) || (policy.PreviousPeriodCues ?? []).Any(c => lower.Contains(c.ToLowerInvariant(), StringComparison.Ordinal));
        var (metric, metricBefore) = MetricOf(before, after);
        return new Context(direction, word is not null || ChangeCue().IsMatch(sentence), previous, TotalCue().IsMatch(sentence),
            PastCue().IsMatch(sentence), metric, good, metricBefore);
    }

    /// <summary>Facts the number may refer to in its context (subjects, period, change, sign, named metric).</summary>
    private static IEnumerable<GroundingFact> Candidates(GroundingFacts facts, IReadOnlyCollection<string>? subjects, Context c, bool negative)
    {
        var scoped = facts.For(subjects).ToList();
        if (subjects is { Count: > 0 } && !c.TotalCue)
        {
            // A statement about monitor A may not use the customer-wide value of a metric monitor A has itself.
            var subjectKeys = scoped.Where(f => f.Subject is not null).Select(f => BaseKey(f.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            scoped = scoped.Where(f => f.Subject is not null || !subjectKeys.Contains(BaseKey(f.Key))).ToList();
        }
        foreach (var f in scoped)
        {
            if (f.Key == ConversationKey) { yield return f; continue; } // a figure repeated from the conversation, in any wording
            if (f.Previous && !c.PreviousCue) continue;
            if (f.IsDelta)
            {
                if (c.Direction != 0 && f.Sign != c.Direction) continue;
                if (c.GoodChange != 0 && f.Sign != c.GoodChange * Polarity(f.Key)) continue;
                if (c.Direction == 0 && !c.ChangeCue) continue;
            }
            else if (c.Direction != 0 || c.GoodChange != 0) continue; // "rose by 3" is a change, not a level
            if (negative ? !(f.Negative || f.Sign < 0) : f.Negative && !c.PastCue) continue;
            if (c.Metric is { Length: > 0 } && !c.Metric.Any(k => f.Key.Contains(k, StringComparison.OrdinalIgnoreCase))
                && !(c.MetricBefore && f.Key.Contains("value", StringComparison.OrdinalIgnoreCase))) continue;
            yield return f;
        }
    }

    private static string BaseKey(string key) => key.StartsWith("previous_", StringComparison.OrdinalIgnoreCase) ? key[9..] : key;

    /// <summary>+1 when a higher value is better (uptime, met targets), -1 when lower is better (downtime, incidents, response times).</summary>
    private static int Polarity(string key) =>
        key.Contains("uptime", StringComparison.OrdinalIgnoreCase) || key.Contains("met", StringComparison.OrdinalIgnoreCase)
        || key.Contains("success", StringComparison.OrdinalIgnoreCase) ? 1 : -1;

    /// <summary>Count nouns named right after a number ("41 incidents", "15 checks", "41 kontrol").</summary>
    private static readonly (string[] Words, string[] Keys)[] CountNouns =
    [
        (["incident", "incidents", "outage", "outages", "olay", "olayı", "olaylar"], ["incident", "outage"]),
        (["kesinti"], ["incident", "outage", "downtime"]),
        (["check", "checks", "kontrol", "kontrolü", "kontroller"], ["check"]),
        (["timeout", "timeouts", "timed", "zaman"], ["timed_out", "timeout"]),
        (["monitor", "monitors", "monitör", "monitörler"], ["monitor"]),
        (["miss", "misses", "missed"], ["miss"]),
        (["failure", "failures", "failed"], ["fail"]),
        (["uptime", "erişilebilirlik", "availability", "available"], ["uptime", "availab"]),
        (["downtime"], ["downtime"]),
    ];

    /// <summary>Metric names shortly before a number ("p95 is 10 s", "timeout from 10 s", "Timeouts were up 41").</summary>
    private static readonly (string[] Words, string[] Keys)[] MetricNames =
    [
        (["p95"], ["p95"]), (["p99"], ["p99"]), (["p50", "median"], ["p50", "median"]),
        (["max", "maximum", "peak"], ["max"]), (["min", "minimum"], ["min"]),
        (["average", "avg", "mean", "ortalama"], ["avg", "average", "mean"]),
        (["timeout", "timeouts", "zaman"], ["timed_out", "timeout"]),
        (["interval", "aralık", "aralığı"], ["interval"]),
        (["threshold", "eşik", "eşiği"], ["threshold"]),
        (["uptime", "erişilebilirlik", "availability"], ["uptime", "availab"]),
        (["downtime"], ["downtime"]),
    ];

    /// <summary>
    /// The metric the text names for this number: the first count noun after it ("41 incidents", "15 checks") or a
    /// metric name shortly before it ("p95 is 10 s"). Null when none is named. <see cref="Context.MetricBefore"/> tells
    /// which: a name before the number does not exclude generic keys ("current_value"), a noun after it does.
    /// </summary>
    private static (string[]? Keys, bool Before) MetricOf(string before, string after)
    {
        static string[]? Find(IEnumerable<string> words, (string[] Words, string[] Keys)[] table)
        {
            foreach (var w in words)
            {
                var lw = w.ToLowerInvariant().TrimEnd('.', ',', ';', ':');
                foreach (var (ws, keys) in table)
                    if (ws.Contains(lw) || (lw.Length > 4 && ws.Any(x => x.Length >= 5 && lw.StartsWith(x, StringComparison.Ordinal)))) return keys;
            }
            return null;
        }
        // The next words up to the next number (that one has its own noun): "14 günde 41 kontrol" names nothing for 14.
        var next = Word().Matches(after).Take(2).Select(x => x.Value).TakeWhile(w => !w.Any(char.IsAsciiDigit));
        if (Find(next, CountNouns) is { } noun) return (noun, false);
        var prev = Word().Matches(before).TakeLast(3).Reverse().Select(x => x.Value);
        return (Find(prev, MetricNames), true);
    }

    private static bool Matches(Value v, GroundingUnit unit, GroundingFact f, GroundingPolicy policy, IReadOnlyList<decimal> thresholds)
    {
        decimal x;
        if (unit == GroundingUnit.None)
        {
            if (f.Unit is not (GroundingUnit.None or GroundingUnit.Count)) return false;
            x = f.Value;
        }
        else if (f.Unit is GroundingUnit.Count or GroundingUnit.None) return false;
        else if (f.Unit == unit) x = f.Value;
        else if (unit is GroundingUnit.Percent or GroundingUnit.PercentagePoints && f.Unit is GroundingUnit.Percent or GroundingUnit.PercentagePoints) x = f.Value;
        else if (policy.ConvertTimeUnits && MsPer(unit) is decimal cu && MsPer(f.Unit) is decimal fu) x = f.Value * fu / cu;
        else return false;

        if (x == v.Number) return true;
        if (!policy.AllowRounding || v.Decimals > 20 || Math.Round(x, v.Decimals, MidpointRounding.AwayFromZero) != v.Number) return false;
        if (policy.GuardZeroAndHundred && v.Number is 0m or 100m) return false; // never rounded to 0 / 100
        if (!RelativeOk(x, v.Number, policy)) return false;
        // Never round across a known threshold: 99.86 is not "99.9" when the target is 99.9.
        return !thresholds.Any(t => (x < t && v.Number >= t) || (x > t && v.Number <= t));
    }

    private static bool RelativeOk(decimal exact, decimal written, GroundingPolicy policy) =>
        exact == written || (exact != 0 && Math.Abs(exact - written) / Math.Abs(exact) <= policy.MaxRelativeRoundingError);

    /// <summary>The sentence around <paramref name="index"/>; "." ends a sentence unless it is a decimal point.</summary>
    private static string Sentence(string text, int index)
    {
        bool IsEnd(int i) => text[i] is '!' or '?' or ';' || (text[i] == '.' && !(i > 0 && i + 1 < text.Length && char.IsAsciiDigit(text[i - 1]) && char.IsAsciiDigit(text[i + 1])));
        var start = 0;
        for (var i = Math.Min(index, text.Length) - 1; i >= 0; i--)
            if (IsEnd(i)) { start = i + 1; break; }
        var end = text.Length;
        for (var i = index; i < text.Length; i++)
            if (IsEnd(i)) { end = i; break; }
        return text[start..end];
    }

    private static bool DateKnown(GroundingFacts f, int year, int month, int day) =>
        month is >= 1 and <= 12 && day is >= 1 and <= 31 && year is >= 1 and <= 9999 && DateTime.DaysInMonth(year, month) >= day
        && f.Dates.Contains(new DateOnly(year, month, day));

    private static bool DayKnown(GroundingFacts f, int day, int month, int? year) =>
        month > 0 && f.Dates.Any(d => d.Day == day && d.Month == month && (year is null || d.Year == year));

    private static int Int(Match m, int group) =>
        int.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1;

    private static int MonthOf(string name)
    {
        var n = name.ToLowerInvariant().TrimEnd('.');
        string[][] months =
        [
            ["january", "jan", "ocak"], ["february", "feb", "şubat"], ["march", "mar", "mart"], ["april", "apr", "nisan"],
            ["may", "mayıs"], ["june", "jun", "haziran"], ["july", "jul", "temmuz"], ["august", "aug", "ağustos"],
            ["september", "sep", "sept", "eylül"], ["october", "oct", "ekim"], ["november", "nov", "kasım"], ["december", "dec", "aralık"],
        ];
        for (var i = 0; i < months.Length; i++)
            if (months[i].Contains(n)) return i + 1;
        return -1;
    }

    private static GroundingUnit UnitOf(string unit)
    {
        var u = unit.ToLowerInvariant();
        if (u.StartsWith("milisaniye", StringComparison.Ordinal)) return GroundingUnit.Milliseconds;
        if (u.StartsWith("saniye", StringComparison.Ordinal)) return GroundingUnit.Seconds;
        if (u.StartsWith("dakika", StringComparison.Ordinal)) return GroundingUnit.Minutes;
        if (u.StartsWith("saat", StringComparison.Ordinal)) return GroundingUnit.Hours;
        if (u.StartsWith("gün", StringComparison.Ordinal)) return GroundingUnit.Days;
        return u switch
        {
            "%" => GroundingUnit.Percent,
            "percentage point" or "percentage points" or "point" or "points" or "puan" or "pp" or "yüzde puan" => GroundingUnit.PercentagePoints,
            "ms" or "millisecond" or "milliseconds" => GroundingUnit.Milliseconds,
            "s" or "sec" or "secs" or "second" or "seconds" or "sn" => GroundingUnit.Seconds,
            "m" or "min" or "mins" or "minute" or "minutes" or "dk" => GroundingUnit.Minutes,
            "h" or "hr" or "hrs" or "hour" or "hours" or "sa" => GroundingUnit.Hours,
            "d" or "day" or "days" => GroundingUnit.Days,
            _ => GroundingUnit.None,
        };
    }

    /// <summary>Milliseconds per time unit (exact integers, so conversions do not drift).</summary>
    private static decimal? MsPer(GroundingUnit u) => u switch
    {
        GroundingUnit.Milliseconds => 1m,
        GroundingUnit.Seconds => 1_000m,
        GroundingUnit.Minutes => 60_000m,
        GroundingUnit.Hours => 3_600_000m,
        GroundingUnit.Days => 86_400_000m,
        _ => null,
    };
}
