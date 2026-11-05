using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Narrative;

/// <summary>
/// AI-6 hard rule: the model produces no numbers, it only interprets the ones it was given. This finds every number in
/// a text (integers, decimals with '.' or the Turkish ',' decimal separator, thousands groups, percentages in both "99.9%"
/// and "%99,9" form, durations such as "2 h 15 min" / "2 saat 15 dakika") and checks each against the numbers of the
/// trusted input facts. Untrusted data blocks never ground a number.
/// <para>
/// Grounding: a value written with d decimals matches a fact x when x rounded to d decimals equals it (rounding is
/// interpretation, inventing is not), except that a rounded value may not become exactly 0 or 100 unless x is (so
/// "99.96 %" can never be written as "100 %"). An ambiguous token ("1,234" = 1234 or 1.234; "3,4" = 3.4 or 3 and 4) is
/// grounded when any reading is. A duration is grounded when its total in minutes is within half its smallest unit of a fact
/// ("2 h 15 min" for 135.4 minutes, "about 2 hours" for 100 to 150 minutes).
/// </para>
/// </summary>
public static partial class NarrativeNumbers
{
    /// <summary>A number found in text; <see cref="Readings"/> are its alternative interpretations (each a list of values).</summary>
    public sealed record Token(string Text, IReadOnlyList<IReadOnlyList<Value>> Readings, double? DurationMinutes = null, double DurationUnitMinutes = 0);

    /// <summary>A numeric value and the number of decimals it was written with.</summary>
    public readonly record struct Value(decimal Number, int Decimals);

    // Names that contain digits but are not quantities: p95, 5xx, HTTP/2, TLS 1.3, IPv4, 24/7.
    [GeneratedRegex(@"(?i)\bp(?:50|75|90|95|99)\b|\b[1-5]xx\b|\bhttps?/[\d.]+|\btls ?v?1\.[0-3]\b|\bipv[46]\b|\b24/7\b")]
    private static partial Regex NonQuantity();

    [GeneratedRegex(@"(?s)<untrusted_data\b.*?</untrusted_data>")]
    private static partial Regex UntrustedBlock();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex Number();

    private const string Unit =
        @"(?:days?|gün|d|hours?|hrs?|saat|sa|h|minutes?|mins?|dakika|dk|m|seconds?|secs?|saniye|sn|s)";

    // One or more "<number> <unit>" parts joined by spaces, "and", "ve" or commas.
    [GeneratedRegex(@"(?i)(?<![\p{L}\d.,])(\d+(?:[.,]\d+)?)\s?(" + Unit + @")(?![\p{L}])(?:(?:\s*,\s*|\s+(?:and|ve)\s+|\s+)(\d+(?:[.,]\d+)?)\s?(" + Unit + @")(?![\p{L}]))*")]
    private static partial Regex Duration();

    /// <summary>Numbers of the trusted facts in a built context (untrusted blocks removed).</summary>
    public static IReadOnlySet<decimal> FactsOf(string userContent)
    {
        var trusted = UntrustedBlock().Replace(userContent ?? "", " ");
        var set = new HashSet<decimal>();
        foreach (var t in Extract(trusted))
            foreach (var reading in t.Readings)
                foreach (var v in reading) set.Add(v.Number);
        return set;
    }

    /// <summary>Every number in <paramref name="text"/>; durations are returned as one token.</summary>
    public static List<Token> Extract(string? text)
    {
        var s = NonQuantity().Replace(text ?? "", " ");
        var tokens = new List<Token>();
        var consumed = new bool[s.Length];
        foreach (Match d in Duration().Matches(s))
        {
            var parts = d.Groups[1].Captures.Concat(d.Groups[3].Captures).Zip(d.Groups[2].Captures.Concat(d.Groups[4].Captures));
            double total = 0, smallest = double.MaxValue;
            var ok = true;
            foreach (var (num, unit) in parts)
            {
                var minutes = UnitMinutes(unit.Value);
                if (minutes is null || !TryParseLoose(num.Value, out var n)) { ok = false; break; }
                total += n * minutes.Value;
                smallest = Math.Min(smallest, minutes.Value);
            }
            if (!ok) continue;
            for (var i = d.Index; i < d.Index + d.Length; i++) consumed[i] = true;
            // The raw numbers stay readings too: "135 min" is grounded by a fact 135 as well.
            var raw = Number().Matches(d.Value).Select(m => Parse(m.Value)).ToList();
            var combined = raw.Count == 1 ? raw[0] : [raw.Select(r => r[0]).SelectMany(v => v).ToList()];
            tokens.Add(new Token(d.Value, combined, total, smallest));
        }
        foreach (Match m in Number().Matches(s))
        {
            if (consumed[m.Index]) continue;
            tokens.Add(new Token(m.Value, Parse(m.Value)));
        }
        return tokens;
    }

    /// <summary>Tokens of <paramref name="text"/> that no fact grounds.</summary>
    public static List<string> Ungrounded(string? text, IReadOnlySet<decimal> facts) =>
        Extract(text).Where(t => !IsGrounded(t, facts)).Select(t => t.Text).ToList();

    public static bool IsGrounded(Token t, IReadOnlySet<decimal> facts)
    {
        if (t.Readings.Any(r => r.All(v => Matches(v, facts)))) return true;
        if (t.DurationMinutes is double total)
            return facts.Any(x => Math.Abs((double)x - total) <= t.DurationUnitMinutes / 2 + 1e-9 && !(total == 0 && x != 0));
        return false;
    }

    public static bool Matches(Value v, IReadOnlySet<decimal> facts)
    {
        if (facts.Contains(v.Number)) return true;
        foreach (var x in facts)
        {
            if (v.Decimals > 28) continue;
            if (Math.Round(x, v.Decimals, MidpointRounding.AwayFromZero) != v.Number) continue;
            if ((v.Number is 0m or 100m) && x != v.Number) continue; // never round up to 100 % / down to 0
            return true;
        }
        return false;
    }

    /// <summary>Alternative readings of a digit token with '.' / ',' separators.</summary>
    public static List<IReadOnlyList<Value>> Parse(string token)
    {
        var readings = new List<IReadOnlyList<Value>>();
        var dots = token.Count(c => c == '.');
        var commas = token.Count(c => c == ',');
        var groups = token.Split(['.', ',']);
        IReadOnlyList<Value> Split() => groups.Select(g => new Value(decimal.Parse(g, CultureInfo.InvariantCulture), 0)).ToList();
        if (dots + commas == 0)
        {
            readings.Add([new Value(decimal.Parse(token, CultureInfo.InvariantCulture), 0)]);
            return readings;
        }
        bool Grouped(IEnumerable<string> g) => g.All(x => x.Length == 3);
        if (dots == 0 || commas == 0)
        {
            var sep = dots > 0 ? '.' : ',';
            if (dots + commas == 1)
            {
                var (a, b) = (groups[0], groups[1]);
                readings.Add([Dec(a, b)]);
                if (b.Length == 3 && a.Length <= 3) readings.Add([Int(a + b)]);
                if (sep == ',') readings.Add(Split()); // "3,4" may be a list
            }
            else if (groups[0].Length <= 3 && Grouped(groups.Skip(1)))
                readings.Add([Int(string.Concat(groups))]);
            else readings.Add(Split()); // versions, dd.MM.yyyy dates: their parts
            return readings;
        }
        // Both separators: the last one is the decimal separator, the other groups thousands.
        var lastIsComma = token.LastIndexOf(',') > token.LastIndexOf('.');
        var dec = lastIsComma ? ',' : '.';
        var thousands = lastIsComma ? '.' : ',';
        var cut = token.LastIndexOf(dec);
        var intPart = token[..cut].Split(thousands);
        if (token[..cut].Contains(dec) == false && intPart[0].Length <= 3 && Grouped(intPart.Skip(1)))
            readings.Add([Dec(string.Concat(intPart), token[(cut + 1)..])]);
        else readings.Add(Split());
        return readings;
    }

    private static Value Int(string digits) => new(decimal.Parse(digits, CultureInfo.InvariantCulture), 0);

    private static Value Dec(string whole, string fraction) =>
        new(decimal.Parse(whole + "." + fraction, CultureInfo.InvariantCulture), fraction.Length);

    private static bool TryParseLoose(string s, out double value) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static double? UnitMinutes(string unit) => unit.ToLowerInvariant() switch
    {
        "d" or "day" or "days" or "gün" => 1440,
        "h" or "hr" or "hrs" or "hour" or "hours" or "saat" or "sa" => 60,
        "m" or "min" or "mins" or "minute" or "minutes" or "dakika" or "dk" => 1,
        "s" or "sec" or "secs" or "second" or "seconds" or "saniye" or "sn" => 1 / 60.0,
        _ => null,
    };
}
