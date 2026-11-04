using System.Net;
using Microsoft.AspNetCore.Html;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>One sidebar entry of the panel layout.</summary>
public static class SidebarLink
{
    /// <param name="path">Current request path without a trailing slash.</param>
    /// <param name="root">Section roots ("/App") match only themselves; other links also match their sub-pages.</param>
    public static IHtmlContent Render(string path, string href, string icon, string text, bool root = false)
    {
        var active = root
            ? path.Equals(href, StringComparison.OrdinalIgnoreCase) || path.Equals(href + "/Index", StringComparison.OrdinalIgnoreCase)
            : path.StartsWith(href, StringComparison.OrdinalIgnoreCase);
        var attrs = active ? " class=\"active\" aria-current=\"page\"" : "";
        var icon_ = (HtmlString)Icons.Get(icon);
        return new HtmlString($"<li><a href=\"{WebUtility.HtmlEncode(href)}\"{attrs}>{icon_.Value}<span>{WebUtility.HtmlEncode(text)}</span></a></li>");
    }
}
