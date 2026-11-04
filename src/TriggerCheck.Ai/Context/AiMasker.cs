using System.Text;
using System.Text.RegularExpressions;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;

namespace TriggerCheck.Ai.Context;

/// <summary>
/// Masks personal data and secrets before anything is sent to the model. Decision K5: only masked technical data leaves
/// the system (host name, status codes, error types, timings). Therefore:
/// <list type="bullet">
/// <item>every URL is reduced to scheme + host (+ port); path, query, fragment and credentials become "/…";</item>
/// <item>key/value secrets (<c>Authorization: Bearer x</c>, <c>{"api_key": "x"}</c>, <c>db_password=x</c>, ...) and bare
/// credential shapes (JWTs, <c>sk-…</c>, AWS access keys, Telegram bot tokens, GitHub / Slack tokens, and any other 32+
/// character token mixing letters and digits) become <see cref="Secret"/>;</item>
/// <item>e-mail addresses and phone numbers become <see cref="Email"/> / <see cref="Phone"/>.</item>
/// </list>
/// Over-masking is accepted (e.g. a long hash in an error text); under-masking is not.
/// </summary>
public static partial class AiMasker
{
    public const string Email = "[email]";
    public const string Phone = "[phone]";
    public const string Secret = "[secret]";
    public const string PathMarker = "/…";

    private const string Keyword =
        "authorization|api[-_]?key|apikey|access[-_]?key|secret|token|passw(?:or)?d|pwd|cookie|session|credentials?|private[-_]?key|signature";

    // key (optionally quoted, any prefix/suffix such as db_password / client_secret / aws_secret_access_key / x-api-key)
    // + ":" or "=" + value: a quoted string, or an optional auth scheme followed by an optionally quoted token.
    [GeneratedRegex(
        @"(?i)(?<![A-Za-z0-9])([""']?[A-Za-z0-9_.\-]*(?:" + Keyword + @")[A-Za-z0-9_\-]*[""']?\s*[:=]\s*)" +
        @"(?:""[^""\r\n]*""|'[^'\r\n]*'|(?:(?:bearer|basic|token|digest)\s+)?[""']?[^\s""'&;,}\])]+[""']?)")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(bearer|basic)\s+[""']?[A-Za-z0-9\-._~+/]{8,}=*[""']?")]
    private static partial Regex SchemeToken();

    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*" +                 // JWT
        @"|(?<![A-Za-z0-9])(?:sk|rk|pk)[-_][A-Za-z0-9_\-]{12,}" +                      // sk-..., sk_live_..., pk_test_...
        @"|(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])" +                      // AWS access key id
        @"|(?<![0-9])\d{6,}:[A-Za-z0-9_\-]{30,}" +                                     // Telegram bot token
        @"|(?<![A-Za-z0-9])(?:ghp|gho|ghs|ghu|ghr|github_pat)_[A-Za-z0-9_]{20,}" +     // GitHub
        @"|(?<![A-Za-z0-9])xox[abposr]-[A-Za-z0-9\-]{10,}")]                         // Slack
    private static partial Regex CredentialShape();

    // Candidate opaque tokens: 32+ chars of base64 / base64url / hex alphabet (no '/' or '.', so URLs and host names
    // are not candidates). Filtered in code: must mix letters and digits and have at most 4 separators (UUIDs yes,
    // hyphenated slugs no).
    [GeneratedRegex(@"(?<![A-Za-z0-9+_=\-])[A-Za-z0-9+_=\-]{32,}(?![A-Za-z0-9+_=\-])")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.\-]*://[^\s""'<>]+")]
    private static partial Regex Url();

    // User info: "scheme://anything@" or, without a scheme, "user:password@host" (e.g. "u:p@db:5432").
    [GeneratedRegex(@"(?i)([a-z][a-z0-9+.\-]*://)[^\s/?#""']+@|(?<![^\s""'(\[=])[^\s/@""':]+:[^\s/@""']+@(?=[a-z0-9.\-\[\]]+)")]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b")]
    private static partial Regex EmailAddress();

    // International (+90 532 123 45 67), NANP ((555) 123-4567 / 555-123-4567) and Turkish national (0532 123 45 67) numbers.
    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}[\s.\-]?(?:\(?\d{1,4}\)?[\s.\-]?){2,5}\d{2,4}\b|\(\d{3}\)\s?\d{3}[\s.\-]\d{4}\b|\b\d{3}[.\-]\d{3}[.\-]\d{4}\b|\b0\d{3}\s?\d{3}\s?\d{2}\s?\d{2}\b")]
    private static partial Regex PhoneNumber();

    /// <summary>
    /// Normalizes (NFKC: fullwidth and other compatibility look-alikes become ASCII), then masks secrets, credentials,
    /// URLs (to scheme + host), e-mail addresses and phone numbers.
    /// </summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Normalize(NormalizationForm.FormKC);
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + Secret);
        s = SchemeToken().Replace(s, m => m.Groups[1].Value + " " + Secret);
        s = CredentialShape().Replace(s, Secret);
        s = UserInfo().Replace(s, m => m.Groups[1].Value);
        s = Url().Replace(s, m => HostOnly(m.Value));
        s = EmailAddress().Replace(s, Email);
        s = PhoneNumber().Replace(s, Phone);
        s = LongToken().Replace(s, m => LooksLikeToken(m.Value) ? Secret : m.Value);
        return s;
    }

    /// <summary>Monitor target: URLs become scheme + host (+ port); Tcp/Ping hosts are kept without credentials.</summary>
    public static string MaskTarget(MonitorType type, string? target) =>
        type is MonitorType.Http or MonitorType.Keyword ? HostOnly(target) : MaskText(target);

    /// <summary>Alert channel target: webhook URLs lose their secret path; e-mail / phone / chat ids are never sent.</summary>
    public static string MaskAlertTarget(AlertChannelType type, string? target) => type switch
    {
        AlertChannelType.Webhook or AlertChannelType.Slack or AlertChannelType.Teams => SecretMasker.MaskWebhookUrl(target),
        AlertChannelType.Email => Email,
        AlertChannelType.Sms or AlertChannelType.VoiceCall => Phone,
        _ => Secret,
    };

    /// <summary>
    /// <c>https://user:pw@shop.example.com:8443/orders/42?x=1</c> → <c>https://shop.example.com:8443/…</c>. Input that
    /// does not parse keeps only "scheme://host" with any user info removed.
    /// </summary>
    public static string HostOnly(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var rest = uri.AbsolutePath.Length > 1 || uri.Query.Length > 1 || uri.Fragment.Length > 1;
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (rest ? PathMarker : "");
        }
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var start = schemeEnd < 0 ? 0 : schemeEnd + 3;
        var end = url.IndexOfAny(['/', '?', '#', '\\'], start);
        var authority = end < 0 ? url[start..] : url[start..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        return url[..start] + authority + (end < 0 ? "" : PathMarker);
    }

    private static bool LooksLikeToken(string s)
    {
        int letters = 0, digits = 0, separators = 0;
        foreach (var c in s)
        {
            if (char.IsAsciiLetter(c)) letters++;
            else if (char.IsAsciiDigit(c)) digits++;
            else if (c is '-' or '_') separators++;
        }
        return letters > 0 && digits > 0 && separators <= 4;
    }
}
