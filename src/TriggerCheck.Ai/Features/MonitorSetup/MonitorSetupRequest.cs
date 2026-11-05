using System.Text;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;

namespace TriggerCheck.Ai.Features.MonitorSetup;

/// <summary>
/// The user's free-text request of the AI-9 assistant, split into what the model may see and what stays here (K5):
/// <list type="bullet">
/// <item>every link becomes a placeholder <c>[U1]</c>; the model only learns its scheme and host (path, query, fragment and
/// credentials stay local), and <see cref="MonitorSetupForm"/> puts the user's own link back into the form;</item>
/// <item>quoted text ("Ödeme", 'ok', “…”) becomes <c>[T1]</c> and is restored locally the same way, so an exact keyword or
/// expected value never leaves the system;</item>
/// <item>the rest goes through <see cref="AiMasker.MaskText"/> (secrets, tokens, e-mail addresses, phone numbers) when
/// <see cref="MonitorSetupPrompts.UserContent"/> puts it into a delimited data block.</item>
/// </list>
/// Placeholders the model invents are ignored; it can only point at text the user typed.
/// </summary>
public sealed partial class MonitorSetupRequest
{
    private readonly Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _quotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The request with links and quoted text replaced by placeholders, not yet masked: always send it through
    /// <see cref="MonitorSetupPrompts.UserContent"/>, which masks the rest.
    /// </summary>
    public string PlaceholderText { get; }

    /// <summary>The request was longer than the configured limit and was cut.</summary>
    public bool Truncated { get; }

    /// <summary>Link placeholders ("U1") → the link as typed.</summary>
    public IReadOnlyDictionary<string, string> Urls => _urls;

    /// <summary>Quote placeholders ("T1") → the quoted text as typed.</summary>
    public IReadOnlyDictionary<string, string> Quotes => _quotes;

    // http(s) links, and scheme-less host names followed by a path ("shop.example.com/checkout?x=1").
    [GeneratedRegex(@"(?i)\bhttps?://[^\s""'<>“”‘’«»]+|(?<![\w@./:-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?::\d{1,5})?/[^\s""'<>“”‘’«»]*")]
    private static partial Regex Link();

    // "…", “…”, «…» and ‘…’ always pair; a ' opens only after a non-letter (so "example.com's" or "com'un" do not open).
    [GeneratedRegex(@"""([^""\r\n]{1,500})""|“([^”\r\n]{1,500})”|«([^»\r\n]{1,500})»|‘([^’\r\n]{1,500})’|(?<![\p{L}\p{N}])'([^'\s][^'\r\n]{0,499})'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\[(?<kind>[UT])(?<n>\d{1,3})\]", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    public MonitorSetupRequest(string? raw, int maxChars)
    {
        var s = (raw ?? "").Normalize(NormalizationForm.FormKC).Trim();
        // Placeholder look-alikes typed by the user would collide with ours.
        s = Placeholder().Replace(s, m => "(" + m.Value[1..^1] + ")");
        if (s.Length > Math.Max(1, maxChars))
        {
            s = s[..Math.Max(1, maxChars)];
            Truncated = true;
        }

        s = Link().Replace(s, m =>
        {
            var link = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            var rest = m.Value[link.Length..];
            return Add(_urls, "U", link) + rest;
        });
        s = Quoted().Replace(s, m =>
        {
            var text = m.Groups.Values.Skip(1).First(g => g.Success).Value;
            if (Placeholder().IsMatch(text)) return m.Value; // a quoted link: already a placeholder
            return "\"" + Add(_quotes, "T", text) + "\"";
        });
        // Masked once, when the user content is built (AiContextBuilder.Untrusted runs AiMasker.MaskText); masking twice
        // would garble "[secret]" markers.
        PlaceholderText = s;
    }

    private static string Add(Dictionary<string, string> map, string kind, string value)
    {
        var existing = map.FirstOrDefault(kv => kv.Value == value).Key;
        if (existing is not null) return "[" + existing + "]";
        var key = kind + (map.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        map[key] = value;
        return "[" + key + "]";
    }

    /// <summary>Scheme + host (+ port) of a link placeholder, as the model sees it.</summary>
    public static string SharedForm(string link) =>
        AiMasker.HostOnly(link.Contains("://", StringComparison.Ordinal) ? link : "https://" + link);

    /// <summary>
    /// Replaces the placeholders in model text with what the user typed. False when the text refers to a placeholder that
    /// does not exist (the model invented it); <paramref name="restored"/> is then null.
    /// </summary>
    /// <param name="linksAsHost">Links become their host name only (names and explanations: no path, query or credentials).</param>
    public bool TryRestore(string? text, out string? restored, bool linksAsHost = false)
    {
        restored = text;
        if (string.IsNullOrEmpty(text)) return true;
        var ok = true;
        var result = Placeholder().Replace(text, m =>
        {
            var key = m.Groups["kind"].Value.ToUpperInvariant() + int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var map = key[0] == 'U' ? _urls : _quotes;
            if (map.TryGetValue(key, out var value)) return linksAsHost && key[0] == 'U' ? HostOf(value) : value;
            ok = false;
            return "";
        });
        restored = ok ? result : null;
        return ok;
    }

    /// <summary>Host name of a link as typed ("https://u:p@shop.example.com/x" → "shop.example.com").</summary>
    public static string HostOf(string link) =>
        Uri.TryCreate(link.Contains("://", StringComparison.Ordinal) ? link : "https://" + link, UriKind.Absolute, out var u) ? u.Host : SharedForm(link);

    /// <summary>A bare placeholder ("U1", "[U1]", "\"[T2]\"") or null.</summary>
    public static string? BarePlaceholder(string? text)
    {
        var t = text?.Trim().Trim('"', '\'').Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (!t.StartsWith('[')) t = "[" + t + "]";
        var m = Placeholder().Match(t);
        return m.Success && m.Length == t.Length ? m.Groups["kind"].Value.ToUpperInvariant() + int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
