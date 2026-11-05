using System.Globalization;
using System.Text.RegularExpressions;

namespace TriggerCheck.Tests.Accessibility;

/// <summary>
/// UI-3: the colour tokens of wwwroot/css/site.css meet WCAG AA in the light and the dark theme: 4.5:1 for text,
/// 3:1 for UI components (form control borders, focus ring). Translucent tokens are composited over the surface.
/// </summary>
public class ContrastTests
{
    private static readonly string CssPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "TriggerCheck.Web", "wwwroot", "css", "site.css");

    private record struct Rgb(double R, double G, double B);

    private static Dictionary<string, string> Block(string css, string selectorPattern)
    {
        var m = Regex.Match(css, selectorPattern + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(m.Success, "CSS block not found: " + selectorPattern);
        return Regex.Matches(m.Groups["body"].Value, @"(--[\w-]+)\s*:\s*([^;]+);")
            .ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value.Trim());
    }

    /// <summary>Light tokens, and dark = light overridden by the dark block (":root" always applies).</summary>
    private static (Dictionary<string, string> Light, Dictionary<string, string> Dark) Themes()
    {
        var css = File.ReadAllText(CssPath);
        var light = Block(css, @":root,\s*\[data-bs-theme=""light""\]");
        var dark = new Dictionary<string, string>(light);
        foreach (var (k, v) in Block(css, @"\n\[data-bs-theme=""dark""\]")) dark[k] = v;
        return (light, dark);
    }

    private static Rgb Hex(string hex)
    {
        hex = hex.TrimStart('#');
        int P(int i) => int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber);
        return new(P(0), P(2), P(4));
    }

    /// <summary>Resolves a token (or literal) to an opaque colour; rgba(...) is composited over <paramref name="under"/>.</summary>
    private static Rgb Resolve(Dictionary<string, string> t, string tokenOrColor, Rgb? under = null)
    {
        var v = tokenOrColor.StartsWith("--") ? t[tokenOrColor] : tokenOrColor;
        var guard = 0;
        while (v.StartsWith("var(") && guard++ < 5) v = t[v[4..^1].Trim()];
        if (v.StartsWith('#')) return Hex(v);
        var m = Regex.Match(v, @"rgba\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)");
        Assert.True(m.Success, $"Unparseable colour {tokenOrColor} = {v}");
        double D(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        var a = D(4);
        var bg = under ?? Resolve(t, "--tc-surface");
        return new(Math.Round(D(1) * a + bg.R * (1 - a)), Math.Round(D(2) * a + bg.G * (1 - a)), Math.Round(D(3) * a + bg.B * (1 - a)));
    }

    private static double Luminance(Rgb c)
    {
        static double Ch(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Ratio(string fg, string bg) => RatioOf(Hex(fg), Hex(bg));

    private static double RatioOf(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void Ratio_matches_the_WCAG_reference_values()
    {
        Assert.Equal(21, Ratio("#000000", "#ffffff"), 2);
        Assert.Equal(1, Ratio("#777777", "#777777"), 2);
        Assert.Equal(4.48, Ratio("#777777", "#ffffff"), 2);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Theme_tokens_meet_WCAG_AA(string theme)
    {
        var (light, dark) = Themes();
        var t = theme == "light" ? light : dark;
        var failures = new List<string>();

        void Check(string fg, string bg, double min, string? bgUnder = null)
        {
            var b = Resolve(t, bg, bgUnder is null ? null : Resolve(t, bgUnder));
            var r = RatioOf(Resolve(t, fg, b), b);
            if (r < min) failures.Add($"{fg} on {bg}: {r:0.00} < {min}");
        }

        string[] backgrounds = ["--tc-bg", "--tc-surface", "--tc-surface-2"];
        string[] texts = ["--tc-text", "--tc-muted", "--tc-brand", "--tc-up", "--tc-down", "--tc-warn", "--tc-idle", "--tc-info", "--tc-placeholder"];
        foreach (var bg in backgrounds)
        {
            foreach (var fg in texts) Check(fg, bg, 4.5);
            Check("--tc-input-border", bg, 3);   // form control boundaries
            Check("--tc-focus", bg, 3);          // focus outline
        }
        // Status badges, alerts and stat icons: the colour on its own soft background.
        foreach (var s in new[] { "up", "down", "warn", "idle" })
        {
            Check($"--tc-{s}", $"--tc-{s}-soft", 4.5);
            Check("--tc-muted", $"--tc-{s}-soft", 4.5);
        }
        Check("--tc-brand", "--tc-brand-soft", 4.5);
        Check("--bs-primary-text-emphasis", "--tc-brand-soft", 4.5);   // .alert-info
        // Sidebar (always dark).
        Check("--tc-sidebar-text", "--tc-sidebar", 4.5);
        Check("--tc-sidebar-dim", "--tc-sidebar", 4.5);

        // Filled buttons and banners: white text in light mode, dark text (#0b1020) in dark mode.
        var onFill = theme == "light" ? "#ffffff" : "#0b1020";
        foreach (var fill in new[] { "--tc-brand", "--tc-up", "--tc-down", "--tc-up-hover", "--tc-down-hover", "--tc-brand-hover" })
            Check(onFill, fill, 4.5);
        if (theme == "light")
            foreach (var banner in new[] { "--tc-warn", "--tc-maint", "--tc-muted" })   // public status page banners (light only)
                Check("#ffffff", banner, 4.5);

        Assert.True(failures.Count == 0, $"{theme} theme:\n  " + string.Join("\n  ", failures));
    }
}
