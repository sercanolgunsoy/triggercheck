namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Absolute links to this app for e-mails, payment callbacks and (later) escalation acknowledgement / heartbeat URLs.
/// The base is <c>Site:PublicBaseUrl</c> (<c>Billing:PublicBaseUrl</c> is still read as a backwards-compatible
/// fallback). Outside Development it is required (<see cref="Validate"/>); in Development an empty value means "the
/// current request's scheme and host".
/// </summary>
public sealed class PublicUrls(IConfiguration config)
{
    public const string Key = "Site:PublicBaseUrl";
    public const string LegacyKey = "Billing:PublicBaseUrl";

    public string? BaseUrl => config[Key] is { Length: > 0 } s ? s.TrimEnd('/') : config[LegacyKey] is { Length: > 0 } b ? b.TrimEnd('/') : null;

    public string Build(HttpRequest request, string pathAndQuery) =>
        (BaseUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}") + pathAndQuery;

    /// <summary>Startup check: outside Development the base URL must be an absolute http(s) URL.</summary>
    public static void Validate(WebApplication app)
    {
        if (app.Environment.IsDevelopment()) return;
        var url = new PublicUrls(app.Configuration).BaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Site:PublicBaseUrl (environment variable Site__PublicBaseUrl) must be set to the public address " +
                                                "of the app, e.g. https://app.example.com. It is used in e-mail links and the payment callback.");
    }
}
