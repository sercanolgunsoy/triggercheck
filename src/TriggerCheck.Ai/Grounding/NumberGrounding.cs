using System.Globalization;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Interpretation;

namespace TriggerCheck.Ai.Grounding;

/// <summary>How strictly <see cref="NumberGrounding"/> matches written numbers against the facts.</summary>
/// <param name="AllowRounding">A value written with d decimals matches a fact that rounds to it ("99.95" for 99.9523).</param>
/// <param name="ConvertTimeUnits">"2 h 15 min" matches 135 minutes, "1.2 s" matches 1200 ms. Off: units must agree.</param>
/// <param name="GuardZeroAndHundred">
/// A written 0 or 100 needs a fact that is exactly 0 / 100 for the same subjects, not a delta, and not a previous-period
/// value unless the sentence refers to the previous period (<paramref name="PreviousPeriodCues"/>).
/// </param>
/// <param name="RejectLinksAndContacts">URLs, domain names, e-mail addresses and phone numbers make the text ungrounded.</param>
public sealed record GroundingPolicy(
    bool AllowRounding = false,
    bool ConvertTimeUnits = false,
    bool GuardZeroAndHundred = false,
    bool RejectLinksAndContacts = false,
    IReadOnlyList<string>? PreviousPeriodCues = null)
{
    /// <summary>Numbers exactly as given, units as given (AI-5).</summary>
    public static readonly GroundingPolicy Strict = new();
}

/// <summary>
/// Shared "numbers come from the database, never from the model" engine (AI-5, AI-6). Every number in a model text must
/// be backed by a typed fact of the input (<see cref="GroundingFacts"/>):
/// <list type="bullet">
/// <item>dates and times are matched as whole values: "2026-03-10", "10.03.2026", "10 Mart", "March 10", "February
/// 2026", "11:58" (so "1:58" never matches "11:58"); a bare year needs a date of that year; IP addresses must appear
/// verbatim;</item>
/// <item>other numbers are parsed in English and Turkish forms ("1,234.5", "1.234,5", "99,95", "%99,9"); a dot or comma
/// is a thousands separator only in "1.234" / "1,234" shapes, so "1.5" is never 15; digits are ASCII only, anything
/// unparseable is ungrounded;</item>
/// <item>a unit must agree with the fact's unit (percent, percentage points, ms / s / min / h / days, counts take no
/// unit); time units are converted when the policy allows it;</item>
/// <item>a number after "rose / fell / increased / dropped (by)" or before "more / fewer / less / higher / lower" (and
/// the Turkish "arttı / azaldı / düştü / yükseldi") must be a delta fact with that direction;</item>
/// <item>with subjects ("monitor A") only those subjects' facts and the global ones count.</item>
/// </list>
/// Text inside untrusted blocks never produces facts (the builder does not record any there).
/// </summary>
public static partial class NumberGrounding
{
    // Names with digits that are not quantities: p95, 5xx, HTTP/2, TLS 1.3, IPv4, 24/7.
    [GeneratedRegex(@"(?i)\bp(?:50|75|90|95|99)\b|\b[1-5]xx\b|\bhttps?/[0-9.]+|\btls ?v?1\.[0-3]\b|\bipv[46]\b|\b24/7\b")]
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

    private const string TimeUnit = @"days?|gün|d|hours?|hrs?|saat|sa|h|minutes?|mins?|dakika|dk|m|seconds?|secs?|saniye|sn|s|ms|milliseconds?|milisaniye";

    // Two or more "<number> <time unit>" parts: "2 h 15 min", "1 gün 3 saat".
    [GeneratedRegex(@"(?i)(?<![\p{L}0-9.,])([0-9]+(?:[.,][0-9]+)?)\s?(" + TimeUnit + @")(?![\p{L}])(?:(?:\s*,\s*|\s+(?:and|ve)\s+|\s+)([0-9]+(?:[.,][0-9]+)?)\s?(" + TimeUnit + @")(?![\p{L}]))+")]
    private static partial Regex MultiDuration();

    // A number (not glued to a preceding letter: "W05", "E1" are ids), with an optional "%" before it and unit after it.
    [GeneratedRegex(@"(?i)(?<![\p{L}0-9.,])(%\s?)?([0-9]+(?:[.,][0-9]+)*)(?![0-9])(?:\s?(%|percentage points?|points?|puan|pp|yüzde puan|" + TimeUnit + @")(?![\p{L}]))?")]
    private static partial Regex Quantity();

    [GeneratedRegex(@"(?i)\b(rose|risen|increased|grew|grown|went up|climbed|jumped|up|fell|fallen|dropped|decreased|declined|went down|shrank|down)\s+(?:by\s+)?(?:about\s+|around\s+|nearly\s+|almost\s+)?$")]
    private static partial Regex DirectionBefore();

    [GeneratedRegex(@"(?i)^\s*(?:(?:puan|adet|kez)\s+)?(more|higher|longer|slower|fewer|less|lower|shorter|faster|arttı|artış gösterdi|yükseldi|azaldı|düştü|geriledi|daha fazla|daha az)\b")]
    private static partial Regex DirectionAfter();

    [GeneratedRegex(@"(?i)\b(?:https?://|www\.)\S+|(?<![\p{L}0-9@.-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?![\p{L}0-9-])")]
    private static partial Regex LinkOrDomain();

    /// <summary>The parts of <paramref name="text"/> with a number, date or time that the facts do not back.</summary>
    /// <param name="subjects">Entities the text is about ("monitor A"); null / empty = all facts count.</param>
    public static List<string> Ungrounded(string? text, GroundingFacts facts, GroundingPolicy policy, IReadOnlyCollection<string>? subjects = null)
    {
        var bad = new List<string>();
        if (string.IsNullOrEmpty(text)) return bad;
        if (policy.RejectLinksAndContacts && HasLinkOrContact(text)) bad.Add("link or contact");
        // Digits of other scripts ("٩٩", fullwidth "９９") cannot be checked: never accepted.
        if (text.Any(c => char.IsDigit(c) && !char.IsAsciiDigit(c))) bad.Add("non-ASCII digits");
        var s = NonQuantity().Replace(text, m => new string(' ', m.Length));
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

        var candidates = facts.For(subjects).ToList();
        foreach (Match m in MultiDuration().Matches(s))
        {
            if (!Free(m)) continue;
            if (!policy.ConvertTimeUnits) continue; // checked part by part below
            Use(m);
            var parts = m.Groups[1].Captures.Concat(m.Groups[3].Captures).Zip(m.Groups[2].Captures.Concat(m.Groups[4].Captures)).ToList();
            decimal total = 0, smallest = decimal.MaxValue; // milliseconds
            var ok = true;
            foreach (var (num, unit) in parts)
            {
                var values = Parse(num.Value);
                if (values.Count == 0 || MsPer(UnitOf(unit.Value)) is not decimal per) { ok = false; break; }
                total += values[0].Number * per;
                smallest = Math.Min(smallest, per);
            }
            var tolerance = policy.AllowRounding ? smallest / 2 : 0;
            if (!ok || !candidates.Any(f => !f.IsDelta && MsPer(f.Unit) is decimal fp && Math.Abs(f.Value * fp - total) <= tolerance))
                bad.Add(m.Value.Trim());
        }

        foreach (Match m in Quantity().Matches(s))
        {
            if (!Free(m)) continue;
            Use(m);
            var unitText = m.Groups[1].Success ? "%" : m.Groups[3].Value;
            var unit = unitText.Length == 0 ? GroundingUnit.None : UnitOf(unitText);
            var values = Parse(m.Groups[2].Value);
            var direction = Direction(s, m);
            var sentence = Sentence(text, m.Index);
            if (values.Count == 0 || !values.Any(v => candidates.Any(f => Matches(v, unit, f, direction, policy, sentence))))
                bad.Add(m.Value.Trim());
        }
        return bad;
    }

    public static bool IsGrounded(string? text, GroundingFacts facts, GroundingPolicy policy, IReadOnlyCollection<string>? subjects = null) =>
        Ungrounded(text, facts, policy, subjects).Count == 0;

    /// <summary>URL, domain name, e-mail address or phone number in <paramref name="text"/>.</summary>
    public static bool HasLinkOrContact(string text)
    {
        if (LinkOrDomain().IsMatch(text)) return true;
        var masked = AiMasker.MaskText(text, longTokens: false);
        return masked.Contains(AiMasker.Email, StringComparison.Ordinal) || masked.Contains(AiMasker.Phone, StringComparison.Ordinal);
    }

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
    /// Readings of a digit token: plain integer; "1.234" / "1,234" (groups of exactly three) as thousands or as a
    /// decimal; any other single "." or "," as a decimal; both separators as "1,234.5" / "1.234,5". Empty when it does
    /// not parse (overflow, odd grouping such as "1.2.3").
    /// </summary>
    public static List<Value> Parse(string token)
    {
        var result = new List<Value>();
        if (token.Length == 0 || token.Any(c => c is not (>= '0' and <= '9' or '.' or ',')) || !char.IsAsciiDigit(token[0]) || !char.IsAsciiDigit(token[^1]))
            return result;
        void Add(string digits, string fraction)
        {
            if (decimal.TryParse(fraction.Length == 0 ? digits : digits + "." + fraction, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                result.Add(new Value(v, fraction.Length));
        }
        var dots = token.Count(c => c == '.');
        var commas = token.Count(c => c == ',');
        if (dots + commas == 0)
        {
            Add(token, "");
            return result;
        }
        if (dots == 0 || commas == 0)
        {
            var sep = dots > 0 ? '.' : ',';
            var groups = token.Split(sep);
            var thousands = groups[0].Length <= 3 && groups.Skip(1).All(g => g.Length == 3);
            if (thousands) Add(string.Concat(groups), "");
            if (groups.Length == 2) Add(groups[0], groups[1]);
            return result;
        }
        var lastComma = token.LastIndexOf(',');
        var lastDot = token.LastIndexOf('.');
        var dec = lastComma > lastDot ? ',' : '.';
        var grp = dec == ',' ? '.' : ',';
        var cut = token.LastIndexOf(dec);
        var intPart = token[..cut];
        if (intPart.Contains(dec)) return result;
        var g2 = intPart.Split(grp);
        if (g2[0].Length <= 3 && g2.Skip(1).All(g => g.Length == 3)) Add(string.Concat(g2), token[(cut + 1)..]);
        return result;
    }

    private static bool Matches(Value v, GroundingUnit unit, GroundingFact f, int direction, GroundingPolicy policy, string sentence)
    {
        if (direction != 0 && f.Sign != direction) return false;
        decimal x;
        if (unit == GroundingUnit.None) x = f.Value;
        else if (f.Unit == GroundingUnit.Count) return false;
        else if (f.Unit == GroundingUnit.None || f.Unit == unit) x = f.Value;
        else if (unit is GroundingUnit.Percent or GroundingUnit.PercentagePoints && f.Unit is GroundingUnit.Percent or GroundingUnit.PercentagePoints) x = f.Value;
        else if (policy.ConvertTimeUnits && MsPer(unit) is decimal cu && MsPer(f.Unit) is decimal fu) x = f.Value * fu / cu;
        else return false;

        var equal = x == v.Number || (policy.AllowRounding && v.Decimals <= 20 && Math.Round(x, v.Decimals, MidpointRounding.AwayFromZero) == v.Number);
        if (!equal) return false;
        // 0 / 100: only a current, non-delta fact that is exactly that value (no rounding up to 100 %, no "0" from a change).
        if (!policy.GuardZeroAndHundred || v.Number is not (0m or 100m)) return true;
        return x == v.Number && !f.IsDelta && (!f.Previous || RefersToPrevious(sentence, policy));
    }

    private static bool RefersToPrevious(string sentence, GroundingPolicy policy)
    {
        var s = sentence.ToLowerInvariant();
        return (policy.PreviousPeriodCues ?? []).Any(c => s.Contains(c.ToLowerInvariant(), StringComparison.Ordinal));
    }

    /// <summary>+1 / -1 when the number is a stated change ("rose by 3", "3 fewer", "3 arttı"), else 0.</summary>
    private static int Direction(string s, Match m)
    {
        var start = Math.Max(0, m.Index - 40);
        var before = s[start..m.Index];
        var cut = before.LastIndexOfAny(['.', ';', ',', '!', '?', ':']);
        if (cut >= 0) before = before[(cut + 1)..];
        var b = DirectionBefore().Match(before);
        var word = b.Success ? b.Groups[1].Value : DirectionAfter().Match(s[(m.Index + m.Length)..]) is { Success: true } a ? a.Groups[1].Value : null;
        return word?.ToLowerInvariant() switch
        {
            null => 0,
            "rose" or "risen" or "increased" or "grew" or "grown" or "went up" or "climbed" or "jumped" or "up" or "more" or "higher" or "longer"
                or "slower" or "arttı" or "artış gösterdi" or "yükseldi" or "daha fazla" => 1,
            _ => -1,
        };
    }

    private static string Sentence(string text, int index)
    {
        var start = text.LastIndexOfAny(['.', '!', '?', ';'], Math.Max(0, Math.Min(index, text.Length - 1))) + 1;
        // "99.9" is not a sentence end: step back over decimal points.
        while (start > 1 && start < text.Length && char.IsAsciiDigit(text[start - 2]) && char.IsAsciiDigit(text[start]))
            start = text.LastIndexOfAny(['.', '!', '?', ';'], start - 2) + 1;
        var end = text.IndexOfAny(['!', '?', ';'], index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static bool DateKnown(GroundingFacts f, int year, int month, int day) =>
        month is >= 1 and <= 12 && day is >= 1 and <= 31 && DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month) >= day
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

    private static GroundingUnit UnitOf(string unit) => unit.ToLowerInvariant() switch
    {
        "%" => GroundingUnit.Percent,
        "percentage point" or "percentage points" or "point" or "points" or "puan" or "pp" or "yüzde puan" => GroundingUnit.PercentagePoints,
        "ms" or "millisecond" or "milliseconds" or "milisaniye" => GroundingUnit.Milliseconds,
        "s" or "sec" or "secs" or "second" or "seconds" or "saniye" or "sn" => GroundingUnit.Seconds,
        "m" or "min" or "mins" or "minute" or "minutes" or "dakika" or "dk" => GroundingUnit.Minutes,
        "h" or "hr" or "hrs" or "hour" or "hours" or "saat" or "sa" => GroundingUnit.Hours,
        "d" or "day" or "days" or "gün" => GroundingUnit.Days,
        _ => GroundingUnit.None,
    };

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
