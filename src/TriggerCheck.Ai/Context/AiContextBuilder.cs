using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TriggerCheck.Ai.Context;

/// <summary>
/// Builds the per-call user content. Trusted facts (computed by TriggerCheck) are written as "key: value" lines;
/// untrusted, externally produced text (probe errors, response snippets) is truncated and wrapped in clearly delimited
/// &lt;untrusted_data&gt; blocks that the system prompt declares to be data, never instructions. Everything is masked
/// with <see cref="AiMasker"/> on the way in, so a context provider cannot forget it.
/// </summary>
public sealed partial class AiContextBuilder
{
    public const int DefaultUntrustedChars = 500;
    public const string TruncatedMarker = " …[truncated]";

    private readonly StringBuilder _sb = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every fact key and untrusted-data source written so far (candidate evidence_refs).</summary>
    public IReadOnlySet<string> Keys => _keys;

    /// <summary>
    /// Typed numbers, dates and literals of the trusted facts written so far (number grounding, AI-5 / AI-6). Recorded
    /// as each fact is written, from the value actually sent; identifiers and untrusted blocks never add facts.
    /// </summary>
    public Grounding.GroundingFacts Facts { get; } = new();

    private string? _subject;

    /// <summary>The entity ("monitor A") the following facts belong to, until the next <see cref="Section"/> or call.</summary>
    public AiContextBuilder Subject(string? subject)
    {
        _subject = subject;
        return this;
    }

    /// <param name="language">UI language of the reader ("en", "tr"); the summary is written in it.</param>
    public AiContextBuilder(string language)
    {
        var name = language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        _sb.Append("response_language: ").AppendLine(name);
    }

    public AiContextBuilder Section(string title)
    {
        _subject = null;
        _sb.AppendLine().Append("## ").AppendLine(Clean(title));
        return this;
    }

    public AiContextBuilder Fact(string key, object? value) => Fact(key, value, identifier: false);

    /// <summary>A fact with an explicit grounding unit (default: from the key's suffix, see <see cref="Grounding.GroundingFacts.UnitOf"/>).</summary>
    public AiContextBuilder Fact(string key, object? value, Grounding.GroundingUnit unit) => Fact(key, value, identifier: false, unit);

    /// <summary>
    /// A change written as "0.124 (down)" / "3 (up)" (no sign: "+0.1234" would be masked as a phone number) and
    /// recorded as a signed delta, so "fell by 3" is grounded only by a decrease of 3.
    /// </summary>
    public AiContextBuilder Delta(string key, double value, Grounding.GroundingUnit? unit = null)
    {
        var abs = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        _keys.Add(Clean(key));
        _sb.Append(Clean(key)).Append(": ").Append(abs).AppendLine(value > 0 ? " (up)" : value < 0 ? " (down)" : " (no change)");
        // "No change" is not a number to quote: a zero change must not ground a written 0.
        if (value != 0) Facts.AddNumber(key, (decimal)Math.Abs(value), unit, _subject, Math.Sign(value), previous: false);
        return this;
    }

    /// <summary>
    /// A system-generated identifier (monitor name, id, feature code): masked like any fact except for the long-token
    /// heuristic, so a long name is not replaced by "[secret]". Never use it for text that came from outside.
    /// </summary>
    public AiContextBuilder Identifier(string key, object? value) => Fact(key, value, identifier: true);

    private AiContextBuilder Fact(string key, object? value, bool identifier, Grounding.GroundingUnit? unit = null)
    {
        var text = value switch
        {
            null => "n/a",
            DateTime d => Iso(d),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        var sent = Clean(AiMasker.MaskText(text, longTokens: !identifier));
        _keys.Add(Clean(key));
        _sb.Append(Clean(key)).Append(": ").AppendLine(sent);
        if (!identifier)
            Facts.AddValue(key, value is string or null ? sent : value, unit, _subject);
        return this;
    }

    /// <summary>Adds external text as a delimited, masked, truncated data block.</summary>
    public AiContextBuilder Untrusted(string source, string? text, int maxChars = DefaultUntrustedChars)
    {
        _keys.Add(Clean(source));
        _sb.Append("<untrusted_data source=\"").Append(Clean(source)).AppendLine("\">")
            .AppendLine(Neutralize(Truncate(AiMasker.MaskText(text), maxChars)))
            .AppendLine("</untrusted_data>");
        return this;
    }

    public string Build() => _sb.ToString();

    /// <summary>
    /// UTC timestamp text. Values read from the database have Kind Unspecified but are UTC (the app stores UTC only), so
    /// only Local values are converted; otherwise the text would depend on the server's time zone.
    /// </summary>
    public static string Iso(DateTime d) =>
        (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(0, maxChars)] + TruncatedMarker;

    /// <summary>
    /// Angle brackets (and their HTML entity forms) become look-alikes, so external text can never close or open a data
    /// block; control and invisible format characters are removed and Unicode line breaks become '\n'. Fullwidth and
    /// other compatibility forms of '&lt;' were already folded to ASCII by the NFKC step in <see cref="AiMasker.MaskText"/>.
    /// </summary>
    private static string Neutralize(string text)
    {
        var s = EntityBracket().Replace(text, m => m.Value.Contains('l', StringComparison.OrdinalIgnoreCase) || m.Value.Contains("60") || m.Value.Contains("3c", StringComparison.OrdinalIgnoreCase) ? "‹" : "›");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '<') sb.Append('‹');
            else if (c is '>') sb.Append('›');
            else if (c is '\n' or '\t') sb.Append(c);
            else if (c is '\r') continue;
            else if (c is '\u2028' or '\u2029' or '\u0085') sb.Append('\n');
            else if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?i)&(?:lt|gt|#0*60|#0*62|#x0*3c|#x0*3e);?")]
    private static partial Regex EntityBracket();

    /// <summary>Single-line, bracket-free text for trusted keys/values (values may still contain monitor names).</summary>
    private static string Clean(string text) => Neutralize(text).Replace('\n', ' ').Replace('\t', ' ');
}
