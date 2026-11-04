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

    /// <param name="language">UI language of the reader ("en", "tr"); the summary is written in it.</param>
    public AiContextBuilder(string language)
    {
        var name = language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "Turkish" : "English";
        _sb.Append("response_language: ").AppendLine(name);
    }

    public AiContextBuilder Section(string title)
    {
        _sb.AppendLine().Append("## ").AppendLine(Clean(title));
        return this;
    }

    public AiContextBuilder Fact(string key, object? value)
    {
        var text = value switch
        {
            null => "n/a",
            DateTime d => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        _sb.Append(Clean(key)).Append(": ").AppendLine(Clean(AiMasker.MaskText(text)));
        return this;
    }

    /// <summary>Adds external text as a delimited, masked, truncated data block.</summary>
    public AiContextBuilder Untrusted(string source, string? text, int maxChars = DefaultUntrustedChars)
    {
        _sb.Append("<untrusted_data source=\"").Append(Clean(source)).AppendLine("\">")
            .AppendLine(Neutralize(Truncate(AiMasker.MaskText(text), maxChars)))
            .AppendLine("</untrusted_data>");
        return this;
    }

    public string Build() => _sb.ToString();

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
