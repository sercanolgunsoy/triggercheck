using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Interpretation;

/// <summary>
/// "Numbers come from the database, never from the model" (AI-5, like AI-6): every number in the model's text must be
/// one of the numbers in the input it was given. Dates and times count when they appear in the input as written
/// (e.g. "2026-03-10" or "11:58" of "2026-03-10T11:58:00Z"); any other number is checked by value, accepting the
/// usual English and Turkish separators ("1,234.5", "1.234,5", "9,1"). Spelled-out numbers are not checked.
/// </summary>
public static partial class AiNumberGrounding
{
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?Z?)?|(?<!\d)\d{1,2}:\d{2}(?::\d{2})?(?!\d)")]
    private static partial Regex DateOrTime();

    /// <summary>A number not glued to a preceding letter ("A1", "E2" and "check #12" ids are not numbers here).</summary>
    [GeneratedRegex(@"(?<![\p{L}\d])\d+(?:[.,]\d+)*")]
    private static partial Regex Number();

    /// <summary>The numeric values written in <paramref name="input"/> (dates and times excluded).</summary>
    public static HashSet<decimal> NumbersIn(string input)
    {
        var set = new HashSet<decimal>();
        // The input is written by TriggerCheck in invariant format: "." is the decimal point, there is no grouping.
        foreach (Match m in Number().Matches(DateOrTime().Replace(input, " ")))
            if (decimal.TryParse(m.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) set.Add(v);
        return set;
    }

    /// <summary>A date and / or time is grounded when each part ("2026-03-09", "19:20") appears in the input.</summary>
    private static bool DateTimeInInput(string value, string input) =>
        value.TrimEnd('Z').Split(['T', ' '], StringSplitOptions.RemoveEmptyEntries).All(part => input.Contains(part, StringComparison.Ordinal));

    /// <summary>Numbers (and dates / times) in <paramref name="text"/> that do not appear in <paramref name="input"/>.</summary>
    public static List<string> Ungrounded(string? text, string input) => Ungrounded(text, input, NumbersIn(input));

    private static List<string> Ungrounded(string? text, string input, HashSet<decimal> allowed)
    {
        var bad = new List<string>();
        if (string.IsNullOrEmpty(text)) return bad;
        // Dates / times quoted from the input are fine; anything else in that shape is checked number by number.
        var rest = DateOrTime().Replace(text, m => DateTimeInInput(m.Value, input) ? " " : m.Value.Replace(':', ' ').Replace('-', ' '));
        foreach (Match m in Number().Matches(rest))
            if (!Candidates(m.Value).Any(allowed.Contains)) bad.Add(m.Value);
        return bad;
    }

    public static bool IsGrounded(string? text, string input) => Ungrounded(text, input).Count == 0;

    /// <summary>
    /// Removes every hypothesis, evidence line, action and unknown whose text has a number that is not in the input.
    /// Null (the output is unusable) when the summary has one.
    /// </summary>
    public static AiInterpretation? Apply(AiInterpretation raw, string input)
    {
        var allowed = NumbersIn(input);
        bool Ok(string? t) => Ungrounded(t, input, allowed).Count == 0;
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

    /// <summary>The values a written number may stand for ("1.234" is 1.234 or 1234; "9,1" is 9.1 or 91).</summary>
    private static IEnumerable<decimal> Candidates(string token)
    {
        var styles = NumberStyles.AllowDecimalPoint;
        var inv = CultureInfo.InvariantCulture;
        if (decimal.TryParse(token.Replace(",", ""), styles, inv, out var a)) yield return a;           // "," groups, "." decimal
        if (decimal.TryParse(token.Replace(".", "").Replace(',', '.'), styles, inv, out var b)) yield return b; // "." groups, "," decimal
        if (decimal.TryParse(token.Replace(".", "").Replace(",", ""), styles, inv, out var c)) yield return c;  // grouping only
    }
}
