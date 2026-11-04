namespace TriggerCheck.Monitoring.Checks;

/// <summary>
/// Parses and evaluates <c>Monitor.ExpectedStatusCodes</c>: a comma separated list of codes, ranges and classes,
/// e.g. <c>"200-299,301"</c>, <c>"200, 204"</c>, <c>"2xx,3xx"</c>. Empty/null means the default <c>200-299</c>.
/// Valid codes are 100-599. The UI can use <see cref="TryParse"/> to validate user input.
/// </summary>
public sealed class StatusCodeMatcher
{
    public const string DefaultSpec = "200-299";
    public static readonly StatusCodeMatcher Default = new([(200, 299)]);

    private readonly (int From, int To)[] _ranges;

    private StatusCodeMatcher((int From, int To)[] ranges) => _ranges = ranges;

    public IReadOnlyList<(int From, int To)> Ranges => _ranges;

    public bool IsMatch(int statusCode)
    {
        foreach (var (from, to) in _ranges)
            if (statusCode >= from && statusCode <= to) return true;
        return false;
    }

    /// <summary>True when any 3xx code is accepted; the HTTP checker then does not follow redirects so the 3xx can be matched.</summary>
    public bool AcceptsRedirectStatus => _ranges.Any(r => r.From <= 399 && r.To >= 300);

    public static StatusCodeMatcher Parse(string? spec) =>
        TryParse(spec, out var m, out var error) ? m : throw new FormatException(error);

    public static bool TryParse(string? spec, out StatusCodeMatcher matcher, out string? error)
    {
        matcher = Default;
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) return true;

        var ranges = new List<(int, int)>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseToken(raw, out var range))
            {
                error = $"'{raw}' is not a valid status code, range (200-299) or class (2xx).";
                return false;
            }
            ranges.Add(range);
        }

        if (ranges.Count == 0)
        {
            error = "No status codes specified.";
            return false;
        }

        matcher = new StatusCodeMatcher(ranges.ToArray());
        return true;
    }

    private static bool TryParseToken(string token, out (int From, int To) range)
    {
        range = default;

        // "2xx" style class
        if (token.Length == 3 && char.IsDigit(token[0]) && (token[1] is 'x' or 'X') && (token[2] is 'x' or 'X'))
        {
            var cls = token[0] - '0';
            range = (cls * 100, cls * 100 + 99);
            return IsValid(range.From) && IsValid(range.To);
        }

        var dash = token.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(token, System.Globalization.NumberStyles.None, null, out var code) || !IsValid(code)) return false;
            range = (code, code);
            return true;
        }

        var left = token[..dash].Trim();
        var right = token[(dash + 1)..].Trim();
        if (!int.TryParse(left, System.Globalization.NumberStyles.None, null, out var from) ||
            !int.TryParse(right, System.Globalization.NumberStyles.None, null, out var to)) return false;
        if (!IsValid(from) || !IsValid(to) || from > to) return false;
        range = (from, to);
        return true;
    }

    private static bool IsValid(int code) => code is >= 100 and <= 599;

    public override string ToString() =>
        string.Join(",", _ranges.Select(r => r.From == r.To ? r.From.ToString() : $"{r.From}-{r.To}"));
}
