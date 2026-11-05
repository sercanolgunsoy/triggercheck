using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Grounding;

/// <summary>Unit of a fact or of a written number. Time units are converted when a policy allows it.</summary>
public enum GroundingUnit
{
    /// <summary>Plain number without a unit: matches only numbers written without a unit.</summary>
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
/// <param name="Value">Absolute value; the sign is in <paramref name="Sign"/> (deltas) or <paramref name="Negative"/>.</param>
/// <param name="Subject">The entity it belongs to ("monitor A"); null = global (customer-wide, period, settings).</param>
/// <param name="Sign">Deltas only: +1 (up) / -1 (down); 0 for a plain fact.</param>
/// <param name="Previous">The value describes the previous period: it counts only when the sentence refers to it.</param>
/// <param name="Negative">A negative plain value (e.g. days left of an expired certificate).</param>
public sealed record GroundingFact(string Key, decimal Value, GroundingUnit Unit, string? Subject, int Sign = 0, bool Previous = false,
    bool Negative = false)
{
    public bool IsDelta => Sign != 0;
}

/// <summary>
/// The typed facts of one model input, collected by <see cref="Context.AiContextBuilder"/> while each trusted fact is
/// written (never by scanning the rendered text, and never from untrusted blocks). Holds numbers with their unit,
/// subject, period and sign, signed deltas, dates / times / months as whole values, verbatim literals (IP addresses)
/// and HTTP status codes (only matched as status codes, never as quantities).
/// <see cref="NumberGrounding"/> checks model output against it.
/// </summary>
public sealed partial class GroundingFacts
{
    private readonly List<GroundingFact> _numbers = [];
    private readonly HashSet<DateOnly> _dates = [];
    private readonly HashSet<(int Hour, int Minute)> _times = [];
    private readonly HashSet<(int Year, int Month)> _months = [];
    private readonly HashSet<string> _literals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _statusCodes = [];

    /// <summary>Response language ("en" / "tr"): decides how "1,000" / "1.000" are read.</summary>
    public string Language { get; set; } = "en";

    public IReadOnlyList<GroundingFact> Numbers => _numbers;
    public IReadOnlySet<DateOnly> Dates => _dates;
    public IReadOnlySet<(int Hour, int Minute)> Times => _times;
    public IReadOnlySet<(int Year, int Month)> Months => _months;
    public IReadOnlySet<string> Literals => _literals;
    public IReadOnlySet<int> StatusCodes => _statusCodes;

    public void AddNumber(string key, decimal value, GroundingUnit? unit = null, string? subject = null, int sign = 0, bool? previous = null) =>
        _numbers.Add(new GroundingFact(key, Math.Abs(value), unit ?? UnitOf(key), subject, Math.Sign(sign),
            previous ?? key.StartsWith("previous_", StringComparison.OrdinalIgnoreCase), Negative: sign == 0 && value < 0));

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

    public void AddStatusCode(int code)
    {
        if (code is >= 100 and <= 599) _statusCodes.Add(code);
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
            : k.EndsWith("_hours", StringComparison.Ordinal) || k.StartsWith("hours_", StringComparison.Ordinal) ? GroundingUnit.Hours
            : k.EndsWith("_days", StringComparison.Ordinal) || k.StartsWith("days_", StringComparison.Ordinal) ? GroundingUnit.Days
            : GroundingUnit.None;
    }

    /// <summary>Keys whose values are labels or codes, not quantities ("error_class": "http_5xx").</summary>
    private static bool IsCodeKey(string key) =>
        key.EndsWith("error_class", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_type", StringComparison.OrdinalIgnoreCase) || key == "type";

    private static bool IsStatusKey(string key) =>
        key.Contains("status_code", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_status_codes", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2})?Z?)?")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}$")]
    private static partial Regex YearMonth();

    [GeneratedRegex(@"(?<![0-9.])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?![0-9.])")]
    private static partial Regex Ip();

    /// <summary>A free-standing invariant number: not part of a word or code ("http_5xx", "W05", "E1").</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_.,])[0-9]+(?:\.[0-9]+)?(?![\p{L}\p{N}_])")]
    private static partial Regex InvariantNumber();

    /// <summary>
    /// Facts of one trusted value written by the context builder: numbers (typed by <paramref name="unit"/> or the key),
    /// dates / times, "yyyy-MM" months, status codes (keys with "status_code"), and for free-form trusted strings
    /// ("200-299", "status=503") their ISO dates, IP literals and free-standing invariant numbers. Code values
    /// ("error_class") add nothing; non-finite doubles are ignored.
    /// </summary>
    public void AddValue(string key, object? value, GroundingUnit? unit, string? subject)
    {
        if (IsCodeKey(key)) return;
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
            case double x when !double.IsFinite(x):
                return;
            case float y when !float.IsFinite(y):
                return;
            case int or long or short or byte or decimal or double or float:
                var dv = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (IsStatusKey(key)) AddStatusCode((int)dv);
                else AddNumber(key, dv, unit, subject);
                return;
        }
        var s = value.ToString() ?? "";
        if (IsStatusKey(key))
        {
            foreach (Match m in InvariantNumber().Matches(s))
                if (int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)) AddStatusCode(code);
            return;
        }
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
