using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Grounding;

/// <summary>Unit of a fact or of a written number. Time units are converted to minutes when a policy allows it.</summary>
public enum GroundingUnit
{
    /// <summary>Unknown / plain number: matches any written number without a contradicting unit.</summary>
    None,
    /// <summary>A count ("incidents"): matches only numbers written without a unit.</summary>
    Count,
    Percent,
    PercentagePoints,
    Milliseconds,
    Seconds,
    Minutes,
    Hours,
    Days,
}

/// <summary>One numeric fact sent to the model.</summary>
/// <param name="Subject">The entity it belongs to ("monitor A"); null = global (customer-wide, period, settings).</param>
/// <param name="Sign">Deltas only: +1 (up) / -1 (down); 0 for a plain fact.</param>
/// <param name="Previous">The value describes the previous period (only used when the text refers to it).</param>
public sealed record GroundingFact(string Key, decimal Value, GroundingUnit Unit, string? Subject, int Sign = 0, bool Previous = false)
{
    public bool IsDelta => Sign != 0;
}

/// <summary>
/// The typed facts of one model input, collected by <see cref="Context.AiContextBuilder"/> while each trusted fact is
/// written (never by scanning the rendered text, and never from untrusted blocks). Holds numbers with their unit,
/// subject and period, signed deltas, dates / times / months as whole values and verbatim literals (IP addresses).
/// <see cref="NumberGrounding"/> checks model output against it.
/// </summary>
public sealed partial class GroundingFacts
{
    private readonly List<GroundingFact> _numbers = [];
    private readonly HashSet<DateOnly> _dates = [];
    private readonly HashSet<(int Hour, int Minute)> _times = [];
    private readonly HashSet<(int Year, int Month)> _months = [];
    private readonly HashSet<string> _literals = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GroundingFact> Numbers => _numbers;
    public IReadOnlySet<DateOnly> Dates => _dates;
    public IReadOnlySet<(int Hour, int Minute)> Times => _times;
    public IReadOnlySet<(int Year, int Month)> Months => _months;
    public IReadOnlySet<string> Literals => _literals;

    public void AddNumber(string key, decimal value, GroundingUnit? unit = null, string? subject = null, int sign = 0, bool? previous = null) =>
        _numbers.Add(new GroundingFact(key, Math.Abs(value), unit ?? UnitOf(key), subject, Math.Sign(sign),
            previous ?? key.StartsWith("previous_", StringComparison.OrdinalIgnoreCase)));

    public void AddDate(DateTime d, bool withTime = true)
    {
        _dates.Add(DateOnly.FromDateTime(d));
        _months.Add((d.Year, d.Month));
        if (withTime) _times.Add((d.Hour, d.Minute));
    }

    public void AddMonth(int year, int month) => _months.Add((year, month));

    public void AddLiteral(string literal)
    {
        if (!string.IsNullOrWhiteSpace(literal)) _literals.Add(literal.Trim());
    }

    /// <summary>Numeric facts of the given subjects plus the global ones (all facts when <paramref name="subjects"/> is empty).</summary>
    public IEnumerable<GroundingFact> For(IReadOnlyCollection<string>? subjects) =>
        subjects is not { Count: > 0 } ? _numbers
        : _numbers.Where(f => f.Subject is null || subjects.Contains(f.Subject, StringComparer.OrdinalIgnoreCase));

    /// <summary>Unit from the key's suffix ("_percent", "_percentage_points", "_ms", "_seconds", "_minutes", "_hours", "_days").</summary>
    public static GroundingUnit UnitOf(string key)
    {
        var k = key.ToLowerInvariant();
        return k.EndsWith("_percentage_points", StringComparison.Ordinal) || k.EndsWith("_points", StringComparison.Ordinal) ? GroundingUnit.PercentagePoints
            : k.EndsWith("_percent", StringComparison.Ordinal) || k is "percent" or "share" || k.EndsWith("_target", StringComparison.Ordinal) ? GroundingUnit.Percent
            : k.EndsWith("_ms", StringComparison.Ordinal) || k == "ms" ? GroundingUnit.Milliseconds
            : k.EndsWith("_seconds", StringComparison.Ordinal) ? GroundingUnit.Seconds
            : k.EndsWith("_minutes", StringComparison.Ordinal) ? GroundingUnit.Minutes
            : k.EndsWith("_hours", StringComparison.Ordinal) ? GroundingUnit.Hours
            : k.EndsWith("_days", StringComparison.Ordinal) ? GroundingUnit.Days
            : GroundingUnit.None;
    }

    [GeneratedRegex(@"[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2})?Z?)?")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}$")]
    private static partial Regex YearMonth();

    [GeneratedRegex(@"(?<![0-9.])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?![0-9.])")]
    private static partial Regex Ip();

    [GeneratedRegex(@"(?<![0-9.,])[0-9]+(?:\.[0-9]+)?(?![0-9])")]
    private static partial Regex InvariantNumber();

    /// <summary>
    /// Facts of one trusted value written by the context builder: numbers (typed by <paramref name="unit"/> or the key),
    /// dates / times, "yyyy-MM" months, and for free-form trusted strings ("200-299", "status=503") their ISO dates, IP
    /// literals and invariant numbers.
    /// </summary>
    public void AddValue(string key, object? value, GroundingUnit? unit, string? subject)
    {
        switch (value)
        {
            case null or bool or Enum:
                return;
            case DateTime d:
                AddDate(d);
                return;
            case DateTimeOffset o:
                AddDate(o.UtcDateTime);
                return;
            case int or long or short or byte or decimal or double or float:
                var dv = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                AddNumber(key, dv, unit, subject);
                return;
        }
        var s = value.ToString() ?? "";
        if (decimal.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
        {
            AddNumber(key, whole, unit, subject);
            return;
        }
        if (YearMonth().IsMatch(s))
        {
            AddMonth(int.Parse(s[..4], CultureInfo.InvariantCulture), int.Parse(s[5..], CultureInfo.InvariantCulture));
            return;
        }
        var rest = IsoDateTime().Replace(s, m =>
        {
            if (DateTime.TryParse(m.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                AddDate(dt, withTime: m.Value.Length > 10);
            return " ";
        });
        rest = Ip().Replace(rest, m => { AddLiteral(m.Value); return " "; });
        foreach (Match m in InvariantNumber().Matches(rest))
            if (decimal.TryParse(m.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                AddNumber(key, n, GroundingUnit.None, subject);
    }
}
