using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>English default, Turkish via the culture cookie, and static guards on the resource files.</summary>
public class LocalizationTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task Default_language_is_English_even_for_a_Turkish_browser()
    {
        var c = f.CreateBrowser();
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9");
        var html = await c.GetStringAsync("/Account/Login");
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("Remember me", html);
        Assert.DoesNotContain("Beni hatırla", html);
    }

    [Fact]
    public async Task Set_language_switches_to_Turkish_and_back()
    {
        var c = f.CreateBrowser();
        var r = await c.GetAsync("/set-language?culture=tr&returnUrl=%2FAccount%2FLogin");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/Account/Login", r.Headers.Location?.OriginalString);

        var tr = await c.GetStringAsync("/Account/Login");
        Assert.Contains("<html lang=\"tr\">", tr);
        Assert.Contains("Beni hatırla", tr);

        await c.SetLanguageAsync("en");
        Assert.Contains("Remember me", await c.GetStringAsync("/Account/Login"));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Set_language_does_not_redirect_off_site(string returnUrl)
    {
        var r = await f.CreateBrowser().GetAsync($"/set-language?culture=tr&returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal("/", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unknown_language_falls_back_to_English()
    {
        var c = f.CreateBrowser();
        await c.SetLanguageAsync("de");
        Assert.Contains("Remember me", await c.GetStringAsync("/Account/Login"));
    }

    [Fact]
    public async Task Login_errors_follow_the_language()
    {
        var en = f.CreateBrowser();
        var r = await en.PostFormAsync("/Account/Login", new() { ["Input.Email"] = "nobody@x.test", ["Input.Password"] = "whatever1" });
        Assert.Contains("Invalid login attempt.", await r.Content.ReadAsStringAsync());

        var tr = f.CreateBrowser();
        await tr.SetLanguageAsync("tr");
        r = await tr.PostFormAsync("/Account/Login", new() { ["Input.Email"] = "nobody@x.test", ["Input.Password"] = "whatever1" });
        Assert.Contains("Geçersiz giriş denemesi.", await r.Content.ReadAsStringAsync());
    }

    // ---- static guards -------------------------------------------------------------------------------------

    private static readonly string Root = FindRoot();
    private static readonly string Web = Path.Combine(Root, "src", "TriggerCheck.Web");

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TriggerCheck.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("TriggerCheck.sln not found");
    }

    private static Dictionary<string, string> Resx(string name) =>
        XDocument.Load(Path.Combine(Web, "Resources", name + ".tr.resx")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Web, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs") || p.EndsWith(".cshtml"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private const string Lit = "\"((?:[^\"\\\\]|\\\\.)*)\"";
    private static readonly Regex[] KeyPatterns =
    [
        new(@"\bL\[\s*" + Lit),                       // L["..."] in views and page models
        new(@"\bl\[\s*" + Lit),                       // l["..."] in LocalizationSetup
        new(@"ErrorMessage\s*=\s*" + Lit),            // data annotation messages
        new(@"Display\([^)]*?\bName\s*=\s*" + Lit),   // [Display(Name = "...")]
        new(@"Display\([^)]*?\bPrompt\s*=\s*" + Lit),
        new(@"\bT\(\s*" + Lit),                       // Fmt
        new(@"nameof\(\w+\),\s*" + Lit),              // LocalizedIdentityErrorDescriber
    ];

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    /// <summary>Every key used in code has a Turkish translation in its area's resource or the shared one.</summary>
    [Fact]
    public void Every_used_key_has_a_Turkish_translation()
    {
        var shared = Resx("SharedResource");
        var app = Resx("AppResource");
        var admin = Resx("AdminResource");
        var sep = Path.DirectorySeparatorChar;

        var missing = new List<string>();
        foreach (var file in SourceFiles())
        {
            var rel = Path.GetRelativePath(Web, file);
            var area = rel.StartsWith($"Areas{sep}App{sep}") ? app : rel.StartsWith($"Areas{sep}SuperAdmin{sep}") ? admin : null;
            var text = File.ReadAllText(file);
            foreach (var rx in KeyPatterns)
            foreach (Match m in rx.Matches(text))
            {
                var key = Unescape(m.Groups[1].Value);
                if (key.Length == 0 || IsNonText(key)) continue;
                if (!shared.ContainsKey(key) && !(area?.ContainsKey(key) ?? false)) missing.Add($"{rel}: \"{key}\"");
            }
        }
        Assert.True(missing.Count == 0, "Missing Turkish translations:\n" + string.Join("\n", missing.Distinct()));
    }

    /// <summary>Keys that need no translation: format-only strings such as "{0} ms".</summary>
    private static bool IsNonText(string key) => !Regex.IsMatch(Regex.Replace(key, @"\{\d+[^}]*\}", ""), "[A-Za-z]{2,}");

    [Fact]
    public void Keys_are_never_interpolated()
    {
        var bad = SourceFiles().Where(p => Regex.IsMatch(File.ReadAllText(p), @"\b[Ll]\[\s*\$""")).Select(p => Path.GetRelativePath(Web, p)).ToList();
        Assert.True(bad.Count == 0, "Use L[\"... {0} ...\", arg] instead of an interpolated key:\n" + string.Join("\n", bad));
    }

    [Theory]
    [InlineData("SharedResource")]
    [InlineData("AppResource")]
    [InlineData("AdminResource")]
    public void Translations_keep_the_placeholders(string resource)
    {
        static string Ph(string s) => string.Join(",", Regex.Matches(s, @"\{\d+(?:[:,][^}]*)?\}").Select(m => m.Value).Order());
        var bad = Resx(resource).Where(kv => Ph(kv.Key) != Ph(kv.Value) || kv.Value.Trim().Length == 0)
            .Select(kv => $"\"{kv.Key}\" → \"{kv.Value}\"").ToList();
        Assert.True(bad.Count == 0, "Placeholder mismatch or empty translation:\n" + string.Join("\n", bad));
    }

    /// <summary>Resource names are case-insensitive: "Suspended" and "suspended" collide and one is silently dropped.</summary>
    [Theory]
    [InlineData("SharedResource")]
    [InlineData("AppResource")]
    [InlineData("AdminResource")]
    public void Keys_are_unique_ignoring_case(string resource)
    {
        var dupes = XDocument.Load(Path.Combine(Web, "Resources", resource + ".tr.resx")).Root!.Elements("data")
            .Select(e => (string)e.Attribute("name")!).GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => string.Join(" / ", g)).ToList();
        Assert.True(dupes.Count == 0, "Keys differing only by case:\n" + string.Join("\n", dupes));
    }

    /// <summary>All Turkish UI text lives in the .tr.resx files; source files contain English only.</summary>
    [Fact]
    public void No_Turkish_text_left_in_web_sources()
    {
        var allowed = new[] { "Türkçe" }; // language switcher shows each language in its own name
        var sep = Path.DirectorySeparatorChar;
        var hits = new List<string>();
        foreach (var file in SourceFiles())
        {
            var rel = Path.GetRelativePath(Web, file);
            if (rel == $"Pages{sep}Shared{sep}_ValidationScriptsPartial.cshtml") continue; // jQuery Validate's Turkish messages
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = allowed.Aggregate(lines[i], (l, a) => l.Replace(a, ""));
                if (Regex.IsMatch(line, "[çğıöşüÇĞİÖŞÜ]")) hits.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "Turkish text in source (move it to a .tr.resx):\n" + string.Join("\n", hits.Take(200)));
    }

    // ---- UI-2: literal text in views ----------------------------------------------------------------------

    /// <summary>Words that may appear as literal text in views: brand, product and protocol names, language names.</summary>
    private static readonly HashSet<string> LiteralAllowed = new(StringComparer.Ordinal)
    {
        "TriggerCheck", "HTTP", "HTTPS", "JSON", "API", "AI", "TXT", "Ping", "iyzico", "English", "Türkçe",
        "central",                          // region id (shown in monospace next to its localized name)
        "CustomerAdmin", "CustomerViewer",  // role names in the SuperAdmin user form
        "acme", "eu",                       // example values in placeholders (status page slug, region id "eu-central")
    };

    /// <summary>
    /// Every piece of user-visible text in a .cshtml (text between tags and the placeholder / title / aria-label /
    /// alt / data-confirm attributes) goes through the localizer. Razor expressions, code blocks, comments and
    /// script / style / svg / pre / code elements are stripped first; what is left must not contain words, apart
    /// from <see cref="LiteralAllowed"/> and technical tokens (URLs, paths, host names).
    /// </summary>
    [Fact]
    public void Views_have_no_literal_text_outside_the_localizer()
    {
        var hits = new List<string>();
        foreach (var file in SourceFiles().Where(p => p.EndsWith(".cshtml")))
        {
            var rel = Path.GetRelativePath(Web, file);
            if (rel.EndsWith("_ValidationScriptsPartial.cshtml")) continue;
            hits.AddRange(LiteralText(File.ReadAllText(file)).Select(t => $"{rel}: \"{t}\""));
        }
        Assert.True(hits.Count == 0, "Literal text in views (use L[\"...\"]):\n" + string.Join("\n", hits.Distinct()));
    }

    [Fact]
    public void Literal_text_scan_finds_unlocalized_text()
    {
        Assert.Equal(["Save changes"], LiteralText("<button class=\"btn\">Save changes</button>"));
        Assert.Equal(["placeholder=Search"], LiteralText("<input placeholder=\"Search\" />"));
        Assert.Empty(LiteralText("@{ var x = \"Hidden\"; }<p>@L[\"Save\"]</p><p>@L[\"e.g. $['k']\"] · @Model.Name</p>"));
        Assert.Empty(LiteralText("@if (Model.Ok) { <b>@L[\"Yes\"]</b> } else { <b>@Html.Raw(string.Format(L[\"No {0}\"].Value, \"<i>\" + x + \"</i>\"))</b> }"));
        Assert.Empty(LiteralText("<span class=\"tag\">TriggerCheck</span><a href=\"/x\">https://status.example.com/feed</a>"));
    }

    private static readonly Regex StrippedBlocks = new(@"@\*.*?\*@|<!--.*?-->|<(script|style|svg|pre|code)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Directive = new(@"(?m)^\s*@(model|page|using|inject|namespace|addTagHelper|removeTagHelper|inherits|layout|implements)\b.*$");
    private static readonly Regex Keyword = new(@"\G@(functions|code|section|if|foreach|for|while|switch|using|else|try|catch|finally|lock|do|await)\b");
    private static readonly Regex TextAttribute = new(@"\b(placeholder|title|aria-label|alt|data-confirm)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
    private static readonly Regex TechnicalToken = new(@"\S*[/.:_@$\[\]#]\S*|&#?\w+;");

    /// <summary>Literal user-visible text left in a Razor view once all Razor code is removed.</summary>
    private static List<string> LiteralText(string razor)
    {
        var t = Directive.Replace(StrippedBlocks.Replace(razor, " "), " ");
        var sb = new System.Text.StringBuilder(t.Length);
        var i = 0;
        while (i < t.Length)
        {
            if (t[i] != '@' || i + 1 >= t.Length || (i > 0 && char.IsLetterOrDigit(t[i - 1]))) { sb.Append(t[i++]); continue; }
            var n = t[i + 1];
            if (n == '@') { sb.Append('@'); i += 2; continue; }
            if (n is '(' or '{') { i = SkipBalanced(t, i + 1); sb.Append(" @x "); continue; }
            var kw = Keyword.Match(t, i);
            if (kw.Success)
            {
                // Keep the markup inside control-flow bodies; drop the header and whole @functions / @code blocks.
                var j = kw.Index + kw.Length;
                while (j < t.Length && char.IsWhiteSpace(t[j])) j++;
                if (j < t.Length && t[j] == '(') j = SkipBalanced(t, j);
                if (kw.Groups[1].Value is "functions" or "code") { var b = t.IndexOf('{', j); j = b < 0 ? t.Length : SkipBalanced(t, b); }
                i = j; sb.Append(' '); continue;
            }
            if (char.IsLetter(n) || n == '_')
            {
                // Implicit expression: identifier chain with member access, calls and indexers (string-aware).
                var j = i + 1;
                while (j < t.Length)
                {
                    if (char.IsLetterOrDigit(t[j]) || t[j] == '_') j++;
                    else if ((t[j] == '.' || (t[j] == '?' && j + 1 < t.Length && t[j + 1] == '.')) && j + 2 < t.Length && (char.IsLetter(t[j + 1]) || char.IsLetter(t[j + 2]))) j += t[j] == '?' ? 2 : 1;
                    else if (t[j] is '(' or '[') j = SkipBalanced(t, j);
                    else break;
                }
                i = j; sb.Append(" @x "); continue;
            }
            sb.Append(t[i++]);
        }
        var markup = sb.ToString();

        var found = new List<string>();
        void Check(string text, string prefix)
        {
            // Text with C# punctuation is code inside a control-flow body (e.g. "} else {"), not markup text.
            if (Regex.IsMatch(text, @"[{};=""]|&&|\|\||\w\.\w+\(")) return;
            var words = Regex.Matches(TechnicalToken.Replace(text.Replace("@x", " "), " "), @"\p{L}{2,}").Select(m => m.Value);
            if (words.Any(w => !LiteralAllowed.Contains(w))) found.Add(prefix + Regex.Replace(text, @"\s+", " ").Trim());
        }
        foreach (Match m in Regex.Matches(markup, @">([^<>]*)<")) Check(m.Groups[1].Value, "");
        foreach (Match m in TextAttribute.Matches(markup)) Check(m.Groups[2].Value, m.Groups[1].Value + "=");
        return found;
    }

    /// <summary>Index after the bracket that closes the one at <paramref name="start"/>, skipping C# string and char literals.</summary>
    private static int SkipBalanced(string s, int start)
    {
        var depth = 0;
        for (var j = start; j < s.Length; j++)
        {
            var c = s[j];
            if (c is '"' or '\'')
            {
                for (j++; j < s.Length && s[j] != c; j++) if (s[j] == '\\') j++;
            }
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && --depth == 0) return j + 1;
        }
        return s.Length;
    }

    /// <summary>Turkish translations do not keep English words (technical tokens such as Ai:Enabled are ignored).</summary>
    [Theory]
    [InlineData("SharedResource")]
    [InlineData("AppResource")]
    [InlineData("AdminResource")]
    public void Translations_contain_no_English_words(string resource)
    {
        var english = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "your", "you", "with", "this", "that", "are", "have", "has", "will", "from", "please", "cannot", "for",
            "incident", "incidents", "alert", "alerts", "dashboard", "settings", "save", "cancel", "delete", "edit", "create",
            "billing", "report", "reports", "maintenance", "details", "enabled", "disabled", "resolved", "ongoing", "upcoming",
            "subscribe", "password", "monitors",
        };
        var bad = Resx(resource)
            .Select(kv => (kv.Key, Words: Regex.Matches(TechnicalToken.Replace(kv.Value, " "), @"\p{L}+").Select(m => m.Value).Where(english.Contains).ToList()))
            .Where(x => x.Words.Count > 0).Select(x => $"\"{x.Key}\": {string.Join(", ", x.Words)}").ToList();
        Assert.True(bad.Count == 0, "English words in Turkish translations:\n" + string.Join("\n", bad));
    }
}
