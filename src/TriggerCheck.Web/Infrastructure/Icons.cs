using Microsoft.AspNetCore.Html;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Inline SVG line icons (24×24, stroke = currentColor), drawn after the Lucide set (ISC licence).
/// Usage in views: <c>@Icons.Get("bell")</c>; unknown names render nothing.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["dashboard"] = "<rect width=\"7\" height=\"9\" x=\"3\" y=\"3\" rx=\"1\"/><rect width=\"7\" height=\"5\" x=\"14\" y=\"3\" rx=\"1\"/><rect width=\"7\" height=\"9\" x=\"14\" y=\"12\" rx=\"1\"/><rect width=\"7\" height=\"5\" x=\"3\" y=\"16\" rx=\"1\"/>",
        ["activity"] = "<path d=\"M22 12h-4l-3 9L9 3l-3 9H2\"/>",
        ["alert"] = "<path d=\"m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3\"/><path d=\"M12 9v4\"/><path d=\"M12 17h.01\"/>",
        ["bell"] = "<path d=\"M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9\"/><path d=\"M10.3 21a1.94 1.94 0 0 0 3.4 0\"/>",
        ["users"] = "<path d=\"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2\"/><circle cx=\"9\" cy=\"7\" r=\"4\"/><path d=\"M22 21v-2a4 4 0 0 0-3-3.87\"/><path d=\"M16 3.13a4 4 0 0 1 0 7.75\"/>",
        ["card"] = "<rect width=\"20\" height=\"14\" x=\"2\" y=\"5\" rx=\"2\"/><path d=\"M2 10h20\"/>",
        ["building"] = "<rect width=\"16\" height=\"20\" x=\"4\" y=\"2\" rx=\"2\"/><path d=\"M9 22v-4h6v4\"/><path d=\"M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01\"/>",
        ["layers"] = "<path d=\"m12 2 10 5-10 5L2 7z\"/><path d=\"m2 17 10 5 10-5\"/><path d=\"m2 12 10 5 10-5\"/>",
        ["list"] = "<path d=\"M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01\"/>",
        ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2m-7.07-14.07 1.41 1.41m11.32 11.32 1.41 1.41M2 12h2m16 0h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41\"/>",
        ["moon"] = "<path d=\"M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z\"/>",
        ["globe"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20\"/>",
        ["logout"] = "<path d=\"M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4\"/><path d=\"m16 17 5-5-5-5\"/><path d=\"M21 12H9\"/>",
        ["menu"] = "<path d=\"M4 6h16M4 12h16M4 18h16\"/>",
        ["check"] = "<path d=\"M20 6 9 17l-5-5\"/>",
        ["x"] = "<path d=\"M18 6 6 18M6 6l12 12\"/>",
        ["zap"] = "<path d=\"M13 2 3 14h9l-1 8 10-12h-9l1-8z\"/>",
        ["clock"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 6v6l4 2\"/>",
        ["shield"] = "<path d=\"M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10\"/><path d=\"m9 12 2 2 4-4\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"8\"/><path d=\"m21 21-4.3-4.3\"/>",
        ["server"] = "<rect width=\"20\" height=\"8\" x=\"2\" y=\"2\" rx=\"2\"/><rect width=\"20\" height=\"8\" x=\"2\" y=\"14\" rx=\"2\"/><path d=\"M6 6h.01M6 18h.01\"/>",
        ["radio"] = "<circle cx=\"12\" cy=\"12\" r=\"2\"/><path d=\"M16.24 7.76a6 6 0 0 1 0 8.49m-8.48-.01a6 6 0 0 1 0-8.49m11.31-2.82a10 10 0 0 1 0 14.14m-14.14 0a10 10 0 0 1 0-14.14\"/>",
        ["mail"] = "<rect width=\"20\" height=\"16\" x=\"2\" y=\"4\" rx=\"2\"/><path d=\"m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7\"/>",
        ["link"] = "<path d=\"M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71\"/><path d=\"M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71\"/>",
        ["chart"] = "<path d=\"M3 3v18h18\"/><path d=\"M18 17V9M13 17V5M8 17v-3\"/>",
        ["history"] = "<path d=\"M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8\"/><path d=\"M3 3v5h5\"/><path d=\"M12 7v5l4 2\"/>",
        ["arrow-right"] = "<path d=\"M5 12h14m-7-7 7 7-7 7\"/>",
        ["globe-lock"] = "<path d=\"M12 2a10 10 0 1 0 10 10\"/><path d=\"M12 2a14.5 14.5 0 0 0 0 20\"/><path d=\"M2 12h12\"/><rect width=\"8\" height=\"5\" x=\"14\" y=\"17\" rx=\"1\"/><path d=\"M20 17v-2a2 2 0 1 0-4 0v2\"/>",
        ["lock"] = "<rect width=\"18\" height=\"11\" x=\"3\" y=\"11\" rx=\"2\"/><path d=\"M7 11V7a5 5 0 0 1 10 0v4\"/>",
        ["plus"] = "<path d=\"M5 12h14M12 5v14\"/>",
        ["chevron-down"] = "<path d=\"m6 9 6 6 6-6\"/>",
        ["languages"] = "<path d=\"m5 8 6 6\"/><path d=\"m4 14 6-6 2-3\"/><path d=\"M2 5h12\"/><path d=\"M7 2h1\"/><path d=\"m22 22-5-10-5 10\"/><path d=\"M14 18h6\"/>",
        ["user"] = "<circle cx=\"12\" cy=\"8\" r=\"5\"/><path d=\"M20 21a8 8 0 0 0-16 0\"/>",
        ["sparkles"] = "<path d=\"m12 3-1.9 5.8a2 2 0 0 1-1.3 1.3L3 12l5.8 1.9a2 2 0 0 1 1.3 1.3L12 21l1.9-5.8a2 2 0 0 1 1.3-1.3L21 12l-5.8-1.9a2 2 0 0 1-1.3-1.3Z\"/>",
        ["gauge"] = "<path d=\"m12 14 4-4\"/><path d=\"M3.34 19a10 10 0 1 1 17.32 0\"/>",
    };

    public static IHtmlContent Get(string name, int size = 18, string? cssClass = null) =>
        Paths.TryGetValue(name, out var body)
            ? new HtmlString($"<svg class=\"icon{(cssClass is null ? "" : " " + cssClass)}\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{body}</svg>")
            : HtmlString.Empty;

    /// <summary>The TriggerCheck mark: a rounded square with a pulse line.</summary>
    public static IHtmlContent Logo(int size = 28) => new HtmlString(
        $"<svg class=\"logo-mark\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 32 32\" aria-hidden=\"true\" focusable=\"false\">" +
        "<defs><linearGradient id=\"tcg\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#6366f1\"/><stop offset=\"1\" stop-color=\"#06b6d4\"/></linearGradient></defs>" +
        "<rect width=\"32\" height=\"32\" rx=\"9\" fill=\"url(#tcg)\"/>" +
        "<path d=\"M5 17h5l3-7 4 13 3-8h7\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>");
}
