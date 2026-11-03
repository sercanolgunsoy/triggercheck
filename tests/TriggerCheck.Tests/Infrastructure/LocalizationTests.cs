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
}
