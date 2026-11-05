using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace TriggerCheck.Tests.Accessibility;

/// <summary>
/// A small static subset of the Lighthouse / axe accessibility audits, run on server-rendered HTML (UI-3):
/// document language, landmarks, skip link, image alternatives, form labels, accessible names of buttons and links,
/// duplicate ids, dangling ARIA id references, table headers and decorative SVGs.
/// </summary>
public static class AccessibilityAudit
{
    private static readonly HashSet<string> UnlabelledInputTypes = new(StringComparer.OrdinalIgnoreCase) { "hidden", "submit", "button", "reset", "image" };
    private static readonly string[] IdRefAttributes = ["aria-describedby", "aria-labelledby", "aria-controls"];

    public static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Returns one message per problem (empty = clean).</summary>
    public static List<string> Check(string html, string expectedLang)
    {
        var doc = Parse(html);
        var problems = new List<string>();
        void Fail(string msg) => problems.Add(msg);

        // <html lang> matches the UI culture.
        var lang = doc.DocumentElement.GetAttribute("lang");
        if (lang != expectedLang) Fail($"<html lang=\"{lang}\"> (expected \"{expectedLang}\")");
        if (string.IsNullOrWhiteSpace(doc.Title)) Fail("Empty <title>");

        // Exactly one main landmark, reachable through a skip link that is the first focusable element.
        var mains = doc.QuerySelectorAll("main, [role=main]").Distinct().ToList();
        if (mains.Count != 1) Fail($"{mains.Count} main landmarks (expected 1)");
        var firstFocusable = doc.Body?.QuerySelector("a[href], button, input:not([type=hidden]), select, textarea, [tabindex]:not([tabindex=\"-1\"])");
        var skip = doc.QuerySelector("a.skip-link");
        if (skip is null) Fail("No skip link (a.skip-link)");
        else
        {
            if (firstFocusable != skip) Fail("The skip link is not the first focusable element");
            var target = skip.GetAttribute("href") ?? "";
            if (mains.Count == 1 && (!target.StartsWith('#') || mains[0].Id != target[1..]))
                Fail($"Skip link points to '{target}', not to the main landmark (#{mains[0].Id})");
            if (string.IsNullOrWhiteSpace(skip.TextContent)) Fail("Skip link has no text");
        }

        // No duplicate ids; ARIA id references resolve.
        foreach (var dup in doc.QuerySelectorAll("[id]").GroupBy(e => e.Id).Where(g => g.Count() > 1))
            Fail($"Duplicate id '{dup.Key}' ({dup.Count()}x)");
        foreach (var attr in IdRefAttributes)
            foreach (var el in doc.QuerySelectorAll($"[{attr}]"))
                foreach (var id in el.GetAttribute(attr)!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (doc.GetElementById(id) is null) Fail($"{attr}=\"{id}\" on {Describe(el)} points to no element");

        // Images.
        foreach (var img in doc.QuerySelectorAll("img").Where(i => !i.HasAttribute("alt")))
            Fail($"<img> without alt: {Describe(img)}");
        foreach (var svg in doc.QuerySelectorAll("svg").Where(s => !IsHidden(s) && s.GetAttribute("role") is not ("presentation" or "none") && !HasOwnName(s)))
            Fail($"<svg> neither aria-hidden nor labelled: {Describe(svg)}");

        // Form fields have a label.
        foreach (var field in doc.QuerySelectorAll("input, select, textarea"))
        {
            if (field.LocalName == "input" && UnlabelledInputTypes.Contains(field.GetAttribute("type") ?? "text")) continue;
            if (!HasLabel(doc, field)) Fail($"Form field without a label: {Describe(field)}");
        }

        // Buttons and links have an accessible name.
        foreach (var el in doc.QuerySelectorAll("button, a[href], [role=button]"))
            if (!IsHidden(el) && string.IsNullOrWhiteSpace(AccessibleName(doc, el)))
                Fail($"{el.LocalName} without an accessible name: {Describe(el)}");
        foreach (var el in doc.QuerySelectorAll("input[type=submit], input[type=button]"))
            if (string.IsNullOrWhiteSpace(el.GetAttribute("value")) && string.IsNullOrWhiteSpace(el.GetAttribute("aria-label")))
                Fail($"Input button without a value: {Describe(el)}");

        // Graphics, meters and charts have a text alternative.
        foreach (var el in doc.QuerySelectorAll("[role=img], [role=progressbar], [role=meter], canvas").Where(e => !IsHidden(e)))
            if (string.IsNullOrWhiteSpace(el.GetAttribute("aria-label")) && !el.HasAttribute("aria-labelledby"))
                Fail($"{el.LocalName} graphic without a text alternative: {Describe(el)}");

        // Data tables have header cells.
        foreach (var table in doc.QuerySelectorAll("table").Where(t => t.GetAttribute("role") is not ("presentation" or "none")))
            if (table.QuerySelector("th") is null) Fail($"<table> without header cells: {Describe(table)}");

        // Toggles announce their state.
        foreach (var t in doc.QuerySelectorAll("[data-bs-toggle=dropdown], [data-bs-toggle=collapse]"))
            if (!t.HasAttribute("aria-expanded")) Fail($"Toggle without aria-expanded: {Describe(t)}");

        // Heading levels never skip (h2 -> h4) going down.
        var previous = 0;
        foreach (var h in doc.QuerySelectorAll("h1, h2, h3, h4, h5, h6").Where(h => !IsHidden(h)))
        {
            var level = h.LocalName[1] - '0';
            if (previous > 0 && level > previous + 1) Fail($"Heading level skipped (h{previous} -> h{level}): {Describe(h)} {h.TextContent.Trim()}");
            previous = level;
        }

        // Lists contain only list items; no positive tabindex; nothing focusable inside aria-hidden; zoom is not blocked.
        foreach (var list in doc.QuerySelectorAll("ul, ol").Where(l => l.GetAttribute("role") is null))
            foreach (var child in list.Children.Where(c => c.LocalName is not ("li" or "script" or "template")))
                Fail($"<{list.LocalName}> has a <{child.LocalName}> child: {Describe(list)}");
        foreach (var el in doc.QuerySelectorAll("[tabindex]").Where(e => int.TryParse(e.GetAttribute("tabindex"), out var t) && t > 0))
            Fail($"Positive tabindex: {Describe(el)}");
        foreach (var el in doc.QuerySelectorAll("[aria-hidden=true] a[href], [aria-hidden=true] button, [aria-hidden=true] input, [aria-hidden=true] select, [aria-hidden=true] textarea"))
            if (el.GetAttribute("tabindex") != "-1") Fail($"Focusable element inside aria-hidden: {Describe(el)}");
        var viewport = doc.QuerySelector("meta[name=viewport]")?.GetAttribute("content") ?? "";
        if (viewport.Contains("user-scalable=no") || viewport.Contains("maximum-scale")) Fail($"Viewport blocks zooming: {viewport}");

        return problems;
    }

    private static bool IsHidden(IElement el)
    {
        for (var e = el; e is not null; e = e.ParentElement)
            if (e.GetAttribute("aria-hidden") == "true" || e.HasAttribute("hidden")) return true;
        return false;
    }

    private static bool HasOwnName(IElement el) =>
        !string.IsNullOrWhiteSpace(el.GetAttribute("aria-label")) || el.HasAttribute("aria-labelledby")
        || el.Children.Any(c => c.LocalName == "title" && !string.IsNullOrWhiteSpace(c.TextContent));

    private static bool HasLabel(IDocument doc, IElement field)
    {
        if (!string.IsNullOrWhiteSpace(field.GetAttribute("aria-label")) || field.HasAttribute("aria-labelledby")) return true;
        if (!string.IsNullOrWhiteSpace(field.GetAttribute("title"))) return true;
        if (field.Closest("label") is not null) return true;
        var id = field.Id;
        return !string.IsNullOrEmpty(id) && doc.QuerySelectorAll("label[for]").Any(l => l.GetAttribute("for") == id && !string.IsNullOrWhiteSpace(l.TextContent));
    }

    /// <summary>aria-labelledby, aria-label, then the visible text (without aria-hidden parts, with img alt), then title.</summary>
    private static string AccessibleName(IDocument doc, IElement el)
    {
        if (el.GetAttribute("aria-labelledby") is { } ids)
            return string.Join(" ", ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(i => doc.GetElementById(i)?.TextContent));
        if (el.GetAttribute("aria-label") is { } label && !string.IsNullOrWhiteSpace(label)) return label;
        var text = VisibleText(el);
        return string.IsNullOrWhiteSpace(text) ? el.GetAttribute("title") ?? "" : text;
    }

    private static string VisibleText(INode node) => node switch
    {
        IElement e when e.GetAttribute("aria-hidden") == "true" => "",
        IElement { LocalName: "img" } img => img.GetAttribute("alt") ?? "",
        IElement { LocalName: "svg" } svg => svg.GetAttribute("aria-label") ?? "",
        IText t => t.Data,
        _ => string.Concat(node.ChildNodes.Select(VisibleText))
    };

    private static string Describe(IElement el)
    {
        // The start tag only, on one line.
        var html = System.Text.RegularExpressions.Regex.Replace(el.OuterHtml, @"\s+", " ");
        var end = html.IndexOf('>');
        if (end > 0) html = html[..(end + 1)];
        return html.Length > 200 ? html[..200] + "…" : html;
    }
}
