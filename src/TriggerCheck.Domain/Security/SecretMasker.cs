namespace TriggerCheck.Domain.Security;

/// <summary>
/// Hides secrets carried in URLs before they are logged, shown to read-only users or sent to third parties (e.g. an LLM).
/// Webhook URLs (Slack, Teams, ...) carry their secret in the path, so only scheme + host survive; ordinary URLs keep
/// their path but lose credentials, query string and fragment.
/// </summary>
public static class SecretMasker
{
    public const string Ellipsis = "…";

    /// <summary><c>https://hooks.slack.com/services/T0/B0/xyz</c> → <c>https://hooks.slack.com/…</c>. Unparseable input → <c>…</c>.</summary>
    public static string MaskWebhookUrl(string? url)
    {
        if (!TryParse(url, out var uri)) return Ellipsis;
        return Authority(uri) + "/" + Ellipsis;
    }

    /// <summary>
    /// <c>https://user:pw@example.com/a/b?token=x#f</c> → <c>https://example.com/a/b?…</c>. Unparseable input is returned
    /// unchanged when it has no query string, otherwise cut at the <c>?</c>.
    /// </summary>
    public static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!TryParse(url, out var uri))
        {
            var q = url.IndexOfAny(['?', '#']);
            return q < 0 ? url : url[..q] + "?" + Ellipsis;
        }
        var hasExtra = uri.Query.Length > 1 || uri.Fragment.Length > 1;
        return Authority(uri) + uri.AbsolutePath + (hasExtra ? "?" + Ellipsis : "");
    }

    private static bool TryParse(string? url, out Uri uri) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!) && !string.IsNullOrEmpty(uri.Host);

    /// <summary>scheme://host[:port] without user info.</summary>
    private static string Authority(Uri uri) =>
        uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
}
