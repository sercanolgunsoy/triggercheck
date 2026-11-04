using System.Globalization;
using System.Text;

namespace TriggerCheck.Ai.Context;

/// <summary>
/// Builds the per-call user content. Trusted facts (computed by TriggerCheck) are written as "key: value" lines;
/// untrusted, externally produced text (probe errors, response snippets) is truncated and wrapped in clearly delimited
/// &lt;untrusted_data&gt; blocks that the system prompt declares to be data, never instructions. Everything is masked
/// with <see cref="AiMasker"/> on the way in, so a context provider cannot forget it.
/// </summary>
public sealed class AiContextBuilder
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

    /// <summary>Angle brackets become look-alikes, so external text can never close or open a data block.</summary>
    private static string Neutralize(string text) => text.Replace('<', '‹').Replace('>', '›');

    /// <summary>Single-line, bracket-free text for trusted keys/values (values may still contain monitor names).</summary>
    private static string Clean(string text) => Neutralize(text.Replace('\r', ' ').Replace('\n', ' '));
}
