using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TriggerCheck.Ai.Context;

namespace TriggerCheck.Ai.Features.MonitorSetup;

/// <summary>
/// The user's free-text request of the AI-9 assistant, split into what the model may see and what stays here (K5). In
/// this order, on the raw text (capped at twice the length limit) before anything is cut:
/// <list type="number">
/// <item>every link becomes a placeholder <c>[U1]</c>: http(s) and any other scheme, and scheme-less hosts (names
/// with Unicode labels, IPv4, <c>[v6]</c>, single-label hosts such as localhost with a port, optional
/// <c>user@</c>) followed by a port, path, query or fragment. The model only learns scheme and host; path, query,
/// fragment and credentials stay local, and <see cref="MonitorSetupForm"/> puts the user's own link back;</item>
/// <item>quoted text ("Ödeme", 'ok', “…”, an unclosed quote runs to the end) becomes <c>[T1]</c> and is restored
/// locally the same way, so an exact keyword or expected value never leaves the system. A quote whose whole content is
/// one link stays that link; a quote that contains more than that is hidden as a whole;</item>
/// <item>a safety net turns any remaining word with a host-like prefix followed by '/', '?' or '#' into a link
/// placeholder too;</item>
/// <item>the word after a credential keyword written in plain words ("password hunter2", "şifre x", "token is y", and
/// the user name when a password is given) becomes <see cref="AiMasker.Secret"/>;</item>
/// <item>placeholder look-alikes the user typed are neutralised, and the text is cut to the length limit (never inside a
/// placeholder).</item>
/// </list>
/// The rest goes through <see cref="AiMasker.MaskText"/> (secrets, tokens, e-mail addresses, phone numbers) when
/// <see cref="MonitorSetupPrompts.UserContent"/> puts it into a delimited data block. Placeholders the model invents are
/// ignored; it can only point at text the user typed.
/// </summary>
public sealed partial class MonitorSetupRequest
{
    // Our placeholders while the text is processed: private-use characters, so user text cannot forge or break them.
    private const char Open = '', Close = '';

    private readonly Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _quotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The request with links and quoted text replaced by placeholders and plain-word credentials masked; the rest is
    /// not masked yet: always send it through <see cref="MonitorSetupPrompts.UserContent"/>, which masks it.
    /// </summary>
    public string PlaceholderText { get; }

    /// <summary>The request was longer than the configured limit and was cut.</summary>
    public bool Truncated { get; }

    /// <summary>Link placeholders ("U1") → the link as typed.</summary>
    public IReadOnlyDictionary<string, string> Urls => _urls;

    /// <summary>Quote placeholders ("T1") → the quoted text as typed.</summary>
    public IReadOnlyDictionary<string, string> Quotes => _quotes;

    /// <summary>The raw input kept: twice the limit (NFKC can expand text; quotes and links that close a bit after the
    /// limit are still recognised). Applied before normalisation.</summary>
    public static int RawCap(int maxChars) => maxChars >= int.MaxValue / 2 ? int.MaxValue : Math.Max(1, maxChars) * 2;

    private const string Stop = @"\s""'<>“”‘’«»" + "";
    private const string Tail = @"[^" + Stop + @"]*";

    // 1. Any scheme ("https://", "postgres://"), or 2. a scheme-less host followed by a port and/or a path, query or
    // fragment: [v6], IPv4, a dotted name with Unicode labels, or a single-label name (letters, e.g. localhost) with a port;
    // an optional "user@" / "user:pass@" in front.
    [GeneratedRegex(
        @"(?i)\b[a-z][a-z0-9+.\-]*://[^" + Stop + @"]+" +
        @"|(?<![\p{L}\p{N}_@./:\-\[])(?:[^" + Stop + @"/@]+@)?" +
        @"(?:(?:\[[0-9a-f:.]+\]|\d{1,3}(?:\.\d{1,3}){3}|(?:[\p{L}\p{N}](?:[\p{L}\p{N}\-]{0,61}[\p{L}\p{N}])?\.)+\p{L}{2,63})" +
        @"(?::\d{1,5}(?:[/?#]" + Tail + @")?|[/?#]" + Tail + @")" +
        @"|(?<![\p{L}\p{N}_@./:\-\[])(?=[\p{L}\p{N}\-]*\p{L})[\p{L}\p{N}][\p{L}\p{N}\-]{0,62}:\d{1,5}(?:[/?#]" + Tail + @")?(?![\p{L}\p{N}.:]))")]
    private static partial Regex Link();

    // Safety net: a word whose host-like prefix (containing '.', ':', '@' or ']') is followed by '/', '?' or '#' and more
    // (a sentence-final "$.status?" is not a link).
    [GeneratedRegex(@"(?<!\S)[^\s" + "" + @"/?#]*[.:@\]][^\s" + "" + @"/?#]*[/?#][^\s" + "" + @"]*[\p{L}\p{N}][^\s" + "" + @"]*")]
    private static partial Regex HostWithRest();

    [GeneratedRegex("(?<kind>[UT])(?<n>\\d+)")]
    private static partial Regex Internal();

    [GeneratedRegex(@"\[(?<kind>[UT])(?<n>\d{1,3})\]", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    // Plain-word credentials: keyword, whitespace, optionally "is" / "=", then the value (":" / "=" forms right after the
    // keyword are left to AiMasker, which masks them).
    private const string CredentialKeywords = @"passwords?|passwd|pwd|pw|pass|parola(?:s[ıi])?|şifre(?:si)?|sifre(?:si)?|tokens?|keys?|secrets?|api[- ]?key";
    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}])(?<kw>" + CredentialKeywords + @")(?<sep>\s+(?:(?:is|=|olarak)\s+)?)(?<value>[^\s" + "" + @"]+)")]
    private static partial Regex PlainCredential();

    [GeneratedRegex(@"(?i)(?<![\p{L}\p{N}])(?<kw>user(?:name)?|login|kullanıcı(?:\s+adı)?|kullanici(?:\s+adi)?)(?<sep>\s+(?:(?:is|=)\s+)?)(?<value>[^\s" + "" + @"]+)")]
    private static partial Regex PlainUser();

    public MonitorSetupRequest(string? raw, int maxChars)
    {
        maxChars = Math.Max(1, maxChars);
        var s = raw ?? "";
        var cap = RawCap(maxChars);
        if (s.Length > cap)
        {
            s = Cut(s, cap);
            Truncated = true;
        }
        s = s.Replace(Open.ToString(), "").Replace(Close.ToString(), "").Normalize(NormalizationForm.FormKC).Trim();

        // 1. links, on the full text
        s = Link().Replace(s, m => LinkPlaceholder(m.Value));
        // 2. quotes; a quote that is exactly one link stays the link
        s = ReplaceQuotes(s);
        // 3. safety net for anything link-like that is left
        s = HostWithRest().Replace(s, m => LinkPlaceholder(m.Value));
        // 4. plain-word credentials
        if (PlainCredential().IsMatch(s))
        {
            s = PlainCredential().Replace(s, m => m.Groups["kw"].Value + m.Groups["sep"].Value + AiMasker.Secret);
            s = PlainUser().Replace(s, m => m.Groups["kw"].Value + m.Groups["sep"].Value + AiMasker.Secret);
        }
        // 5. user-typed look-alikes of our placeholders (outside links / quotes), then the length limit
        s = Placeholder().Replace(s, m => "(" + m.Value[1..^1] + ")");
        if (s.Length > maxChars)
        {
            s = CutOutsidePlaceholder(s, maxChars);
            Truncated = true;
        }
        Prune(s);
        PlaceholderText = Internal().Replace(s, m => "[" + m.Groups["kind"].Value + m.Groups["n"].Value + "]");
    }

    private string LinkPlaceholder(string value)
    {
        var link = value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        // "[::1]" style hosts end with ']' that belongs to the link.
        if (link.Length < value.Length && value[link.Length] == ']' && link.Contains('[') && !link.Contains(']')) link = value[..(link.Length + 1)];
        if (link.Length == 0) return value;
        return Add(_urls, "U", link) + value[link.Length..];
    }

    private static readonly Dictionary<char, char> Pairs = new() { ['"'] = '"', ['“'] = '”', ['«'] = '»', ['‘'] = '’', ['\''] = '\'' };

    /// <summary>Quotes in reading order; an unclosed quote runs to the end of the text.</summary>
    private string ReplaceQuotes(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            // A ' opens only at the start, after whitespace or an opening bracket: "example.com's", "com'un" and a link
            // placeholder followed by a Turkish suffix ("[U1]'in") are apostrophes.
            var opens = Pairs.ContainsKey(c) && (c != '\'' || i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '(' or '[' or '{');
            if (!opens || i + 1 >= s.Length || (c == '\'' && char.IsWhiteSpace(s[i + 1])))
            {
                sb.Append(c);
                i++;
                continue;
            }
            var close = s.IndexOf(Pairs[c], i + 1);
            if (c == '“' && close < 0) close = s.IndexOf('"', i + 1);
            var end = close < 0 ? s.Length : close;
            var inner = s[(i + 1)..end];
            var trimmed = inner.Trim();
            if (trimmed.Length == 0)
                sb.Append(s, i, (close < 0 ? end : end + 1) - i);
            else if (Internal().Match(trimmed) is { Success: true } only && only.Length == trimmed.Length && only.Groups["kind"].Value == "U")
                sb.Append(c).Append(inner).Append(close < 0 ? "" : s[close].ToString()); // exactly one link: keep it
            else
                sb.Append('"').Append(Add(_quotes, "T", Expand(inner))).Append('"');
            i = close < 0 ? s.Length : close + 1;
        }
        return sb.ToString();
    }

    /// <summary>The text with our internal placeholders replaced by what they stand for (the quote as typed).</summary>
    private string Expand(string text) =>
        Internal().Replace(text, m => (m.Groups["kind"].Value == "U" ? _urls : _quotes)[m.Groups["kind"].Value + m.Groups["n"].Value]);

    /// <summary>Links and quotes that are no longer in the text (inside a hidden quote, or cut off) are not listed.</summary>
    private void Prune(string text)
    {
        var used = Internal().Matches(text).Select(m => m.Groups["kind"].Value + m.Groups["n"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _urls.Keys.Where(k => !used.Contains(k)).ToList()) _urls.Remove(key);
        foreach (var key in _quotes.Keys.Where(k => !used.Contains(k)).ToList()) _quotes.Remove(key);
    }

    private static string Cut(string s, int length)
    {
        if (length < s.Length && length > 0 && char.IsHighSurrogate(s[length - 1])) length--;
        return s[..length];
    }

    private static string CutOutsidePlaceholder(string s, int length)
    {
        var open = s.LastIndexOf(Open, Math.Min(length, s.Length - 1));
        if (open >= 0 && open < length)
        {
            var close = s.IndexOf(Close, open);
            if (close < 0 || close >= length) length = open; // never cut a placeholder in half
        }
        return Cut(s, length).TrimEnd();
    }

    private string Add(Dictionary<string, string> map, string kind, string value)
    {
        var existing = map.FirstOrDefault(kv => kv.Value == value).Key;
        if (existing is not null) return Open + existing + Close;
        // Numbering stays unique even after pruning (keys are never reused).
        var n = (kind == "U" ? ++_lastUrl : ++_lastQuote).ToString(CultureInfo.InvariantCulture);
        map[kind + n] = value;
        return Open + kind + n + Close;
    }

    private int _lastUrl, _lastQuote;

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
            var key = m.Groups["kind"].Value.ToUpperInvariant() + int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
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
        return m.Success && m.Length == t.Length ? m.Groups["kind"].Value.ToUpperInvariant() + int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : null;
    }
}
