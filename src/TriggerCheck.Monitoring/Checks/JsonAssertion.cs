using System.Globalization;
using System.Text;
using System.Text.Json;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>One step of a <see cref="JsonPath"/>: an object member (<see cref="Name"/>) or an array element (<see cref="Index"/>).</summary>
public readonly record struct JsonPathSegment(string? Name, int? Index)
{
    public override string ToString() => Name is not null ? "." + Name : $"[{Index}]";
}

/// <summary>
/// Deliberately small JSONPath subset: <c>$</c>, <c>.name</c>, <c>['name']</c> / <c>["name"]</c> (quoted, with
/// backslash escapes) and <c>[0]</c> (non-negative index). Wildcards, recursive descent, slices, filters and script
/// expressions are rejected, so evaluation is a plain walk with no user-controlled code or unbounded work.
/// </summary>
public static class JsonPath
{
    public const int MaxLength = 500;
    public const int MaxSegments = 32;
    private const int MaxIndex = 1_000_000;

    public static bool TryParse(string? path, out IReadOnlyList<JsonPathSegment> segments, out string error)
    {
        segments = [];
        var list = new List<JsonPathSegment>();
        var p = path?.Trim() ?? "";
        if (p.Length == 0) { error = "JSON path is empty"; return false; }
        if (p.Length > MaxLength) { error = $"JSON path is longer than {MaxLength} characters"; return false; }
        if (p[0] != '$') { error = "JSON path must start with $"; return false; }

        var i = 1;
        while (i < p.Length)
        {
            if (list.Count >= MaxSegments) { error = $"JSON path has more than {MaxSegments} segments"; return false; }
            var c = p[i];
            if (c == '.')
            {
                var start = ++i;
                while (i < p.Length && p[i] != '.' && p[i] != '[') i++;
                var name = p[start..i];
                if (name.Length == 0) { error = "Empty member name (recursive descent '..' is not supported)"; return false; }
                if (name.Any(ch => char.IsWhiteSpace(ch) || ch is '*' or '@' or '?' or '(' or ')' or '\'' or '"' or ']' or '$'))
                {
                    error = $"Unsupported character in member name '{name}' (use ['...'] for special names; wildcards and filters are not supported)";
                    return false;
                }
                list.Add(new JsonPathSegment(name, null));
            }
            else if (c == '[')
            {
                i++;
                if (i < p.Length && p[i] is '\'' or '"')
                {
                    if (!TryReadQuoted(p, ref i, out var name, out error)) return false;
                    list.Add(new JsonPathSegment(name, null));
                }
                else
                {
                    var start = i;
                    while (i < p.Length && char.IsAsciiDigit(p[i])) i++;
                    if (i == start || !int.TryParse(p.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > MaxIndex)
                    {
                        error = "Only [index] (0 or greater) and ['name'] are supported inside brackets (no wildcards, slices, filters or scripts)";
                        return false;
                    }
                    list.Add(new JsonPathSegment(null, index));
                }
                if (i >= p.Length || p[i] != ']') { error = "Missing ] in JSON path"; return false; }
                i++;
            }
            else
            {
                error = $"Unexpected character '{c}' in JSON path (expected . or [)";
                return false;
            }
        }

        segments = list;
        error = "";
        return true;
    }

    /// <summary>Reads a quoted name starting at the quote at <paramref name="i"/>; leaves <paramref name="i"/> after the closing quote.</summary>
    private static bool TryReadQuoted(string p, ref int i, out string name, out string error)
    {
        var quote = p[i++];
        var sb = new StringBuilder();
        while (i < p.Length && p[i] != quote)
        {
            var ch = p[i++];
            if (ch != '\\') { sb.Append(ch); continue; }
            if (i >= p.Length) break;
            var e = p[i++];
            switch (e)
            {
                case '\\' or '\'' or '"' or '/': sb.Append(e); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when i + 4 <= p.Length && ushort.TryParse(p.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default:
                    name = "";
                    error = $"Invalid escape \\{e} in JSON path";
                    return false;
            }
        }
        if (i >= p.Length) { name = ""; error = "Unterminated quoted name in JSON path"; return false; }
        i++; // closing quote
        name = sb.ToString();
        error = "";
        return true;
    }

    /// <summary>Walks <paramref name="root"/>; false when a member or element does not exist (or has the wrong kind).</summary>
    public static bool TrySelect(JsonElement root, IReadOnlyList<JsonPathSegment> segments, out JsonElement value)
    {
        value = root;
        foreach (var s in segments)
        {
            if (s.Name is { } name)
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value)) return false;
            }
            else
            {
                var index = s.Index!.Value;
                if (value.ValueKind != JsonValueKind.Array || index >= value.GetArrayLength()) return false;
                value = value[index];
            }
        }
        return true;
    }
}

/// <summary>Evaluates a JsonApi monitor's assertion. All messages are customer-visible, short and contain no secrets.</summary>
public static class JsonAssertion
{
    private const int MaxShownValue = 100;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 64 };

    /// <summary>Configuration error of a JsonApi monitor, or null.</summary>
    public static string? ValidateConfig(string? path, AssertionOperator? op, string? expected)
    {
        if (!JsonPath.TryParse(path, out _, out var pathError)) return "Invalid JSON path: " + pathError;
        if (op is not { } o || !Enum.IsDefined(o)) return "JSON assertion operator is not configured";
        if (o != AssertionOperator.Exists && expected is null) return "Expected value is not configured";
        if (o is AssertionOperator.LessThan or AssertionOperator.GreaterThan && !TryParseNumber(expected, out _))
            return "Expected value must be a number for < and >";
        return null;
    }

    /// <summary>Null when <paramref name="body"/> satisfies the assertion, otherwise the failure reason.</summary>
    public static string? Evaluate(string body, string path, AssertionOperator op, string? expected)
    {
        if (ValidateConfig(path, op, expected) is { } configError) return configError;
        JsonPath.TryParse(path, out var segments, out _);
        path = path.Trim();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, DocumentOptions); }
        catch (JsonException) { return "Response is not valid JSON"; }

        using (doc)
        {
            if (!JsonPath.TrySelect(doc.RootElement, segments, out var value))
                return $"JSON path {path} not found in response";

            var expectedText = expected ?? "";
            return op switch
            {
                AssertionOperator.Exists => null,
                AssertionOperator.Equal => IsEqual(value, expectedText) ? null : $"JSON value at {path} is {Show(value)} (expected {Quote(expectedText)})",
                AssertionOperator.Contains => Contains(value, expectedText) ? null : $"JSON value at {path} is {Show(value)} (expected it to contain {Quote(expectedText)})",
                AssertionOperator.LessThan or AssertionOperator.GreaterThan => Compare(value, path, op, expectedText),
                _ => "JSON assertion operator is not supported"
            };
        }
    }

    private static string? Compare(JsonElement value, string path, AssertionOperator op, string expected)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var actual))
            return $"JSON value at {path} is {Show(value)}, not a number";
        TryParseNumber(expected, out var limit);
        var ok = op == AssertionOperator.LessThan ? actual < limit : actual > limit;
        return ok ? null : $"JSON value at {path} is {Show(value)} (expected {(op == AssertionOperator.LessThan ? "<" : ">")} {expected.Trim()})";
    }

    /// <summary>
    /// String: exact (ordinal). Number: numeric (1 == 1.0). true/false/null: by literal. Object/array: the expected text
    /// is parsed as JSON and compared structurally.
    /// </summary>
    public static bool IsEqual(JsonElement value, string expected) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == expected,
        JsonValueKind.Number => TryParseNumber(expected, out var n) && NumberEquals(value, n, expected),
        JsonValueKind.True => string.Equals(expected.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.False => string.Equals(expected.Trim(), "false", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Null => string.Equals(expected.Trim(), "null", StringComparison.OrdinalIgnoreCase),
        _ => StructurallyEqual(value, expected)
    };

    /// <summary>String: substring (case-insensitive). Array: any element equals the expected value. Otherwise: the JSON text contains it.</summary>
    public static bool Contains(JsonElement value, string expected) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(expected, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Array => value.EnumerateArray().Any(e => IsEqual(e, expected)),
        _ => value.GetRawText().Contains(expected, StringComparison.OrdinalIgnoreCase)
    };

    private static bool NumberEquals(JsonElement value, double expected, string expectedText)
    {
        // Prefer exact decimal comparison (0.1 + representation issues); fall back to double for huge/tiny values.
        if (value.TryGetDecimal(out var d) && decimal.TryParse(expectedText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) return d == e;
        return value.TryGetDouble(out var actual) && actual == expected;
    }

    private static bool StructurallyEqual(JsonElement value, string expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(expected, DocumentOptions);
            return JsonElement.DeepEquals(value, doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseNumber(string? s, out double value) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string Show(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? Quote(value.GetString()!)
        : Cut(value.GetRawText());

    private static string Quote(string s) => "\"" + Cut(s) + "\"";

    private static string Cut(string s) => s.Length <= MaxShownValue ? s : s[..MaxShownValue] + "…";
}
