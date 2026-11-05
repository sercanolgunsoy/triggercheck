using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Accessibility;
using TriggerCheck.Tests.Ai.Features;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Tests.StatusPages;
using TriggerCheck.Tests.SuperAdmin;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.Infrastructure;

/// <summary>
/// UI-2: key pages of every layout rendered in Turkish contain no English text. Every visible text node and
/// user-facing attribute is checked for (1) an English resource key that fell back untranslated and (2) common
/// English words. Arranged data uses neutral names so it cannot trip the check.
/// </summary>
public class TurkishCoverageTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private sealed record Tenant(int CustomerId, string Admin, int UpId, int DownId, int IncidentId, int MaintenanceId, int StatusPageId, string Slug);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Tenant? _tenant;

    private async Task<Tenant> TenantAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_tenant is not null) return _tenant;
            var (cid, admin, _) = await f.AddTenantAsync("trcov");
            var now = f.Now;
            var up = await f.AddMonitorAsync(cid, "Trcov web", m => { m.Status = MonitorStatus.Up; m.CreatedAtUtc = now.AddDays(-10); });
            var down = await f.AddMonitorAsync(cid, "Trcov api", m => { m.Status = MonitorStatus.Down; m.LastError = "HTTP 503"; m.CreatedAtUtc = now.AddDays(-10); });
            await f.AddMonitorAsync(cid, "Trcov durdu", m => m.Status = MonitorStatus.Paused);
            await f.AddMonitorAsync(cid, "Trcov bekliyor", m => m.Status = MonitorStatus.Unknown);
            for (var i = 0; i < 30; i++)
            {
                await f.AddCheckAsync(up, now.AddMinutes(-10 * i), up: true, ms: 120 + i);
                await f.AddCheckAsync(down, now.AddMinutes(-10 * i), up: i > 3, ms: 300);
            }
            await f.AddIncidentAsync(up, now.AddDays(-2), now.AddDays(-2).AddMinutes(20), cause: "HTTP 500");
            var incident = await f.AddIncidentAsync(down, now.AddMinutes(-35), null, cause: "HTTP 503");
            var window = await f.AddMaintenanceAsync(cid, "Trcov bakım", now.AddDays(1), now.AddDays(1).AddHours(2), up);
            var page = await f.AddStatusPageAsync(cid, "trcov", [up, down], p => p.Announcement = "Cuma günü bakım");
            await f.EvictAsync();
            return _tenant = new Tenant(cid, admin, up, down, incident, window, page, "trcov");
        }
        finally { Gate.Release(); }
    }

    // ---- detection -----------------------------------------------------------------------------------------

    /// <summary>English source texts whose Turkish translation differs: seeing one in a Turkish page means a fallback.</summary>
    private static readonly Lazy<HashSet<string>> EnglishKeys = new(LoadEnglishKeys);

    private static HashSet<string> LoadEnglishKeys()
    {
        var dir = Path.Combine(FindRoot(), "src", "TriggerCheck.Web", "Resources");
        return Directory.EnumerateFiles(dir, "*.tr.resx")
            .SelectMany(p => XDocument.Load(p).Root!.Elements("data"))
            .Select(e => (Key: (string)e.Attribute("name")!, Value: (string)e.Element("value")!))
            .Where(kv => kv.Key != kv.Value && !kv.Key.Contains('{') && Regex.IsMatch(kv.Key, "[A-Za-z]{3,}"))
            .Select(kv => Normalize(kv.Key))
            .Where(k => !Allowed.Contains(k))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TriggerCheck.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("TriggerCheck.sln not found");
    }

    /// <summary>Words that are English and never Turkish (short ambiguous ones such as "on", "at", "an" are left out).</summary>
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "your", "you", "with", "this", "that", "are", "have", "has", "will", "been", "from", "which",
        "when", "there", "please", "cannot", "here", "about", "after", "before", "every", "only", "for", "was", "were", "incidents",
        "yet", "any", "into", "per", "its", "our", "than", "then", "them", "they", "what", "who", "how", "why", "is", "to", "of",
        "save", "cancel", "delete", "edit", "create", "back", "add", "remove", "settings", "monitors", "incident",
        "alert", "alerts", "dashboard", "users", "billing", "reports", "maintenance", "details", "overview", "sign", "log", "new",
        "none", "never", "today", "yesterday", "minutes", "hours", "days", "ago", "enabled", "disabled", "active", "resolved",
        "ongoing", "upcoming", "subscribe", "unsubscribe", "change", "password", "email", "name", "pricing", "features", "website", "database", "checkout",
    };

    /// <summary>Brand, product and technical names that are the same in both languages.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "English", "TriggerCheck",
    };

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static readonly HashSet<string> SkippedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "code", "pre", "kbd", "samp", "noscript", "svg",
    };

    private static readonly Regex TechToken = new(@"\S*[/@.:_=]\S*|&\w+;", RegexOptions.Compiled);

    /// <summary>Finds English text in a page rendered in Turkish.</summary>
    public static List<string> FindEnglish(string html)
    {
        var doc = AccessibilityAudit.Parse(html);
        var found = new List<string>();

        void Check(string raw, string where)
        {
            var text = Normalize(raw);
            if (text.Length == 0) return;
            if (EnglishKeys.Value.Contains(text) || EnglishKeys.Value.Contains(text.TrimEnd(':', '.', '…', '?', '!')))
            {
                found.Add($"untranslated key \"{text}\" ({where})");
                return;
            }
            var words = Regex.Matches(TechToken.Replace(text, " "), @"\p{L}+").Select(m => m.Value);
            var english = words.Where(EnglishWords.Contains).ToList();
            if (english.Count > 0) found.Add($"English word(s) {string.Join(", ", english.Distinct())} in \"{text}\" ({where})");
        }

        foreach (var node in doc.DocumentElement.Descendants())
        {
            if (node is IText t)
            {
                if (t.ParentElement is { } p && (p.Closest(string.Join(",", SkippedElements)) is not null || p.Closest("[translate=no],.font-monospace") is not null)) continue;
                Check(t.Data, "<" + (t.ParentElement?.LocalName ?? "?") + ">");
            }
            else if (node is IElement el && el.Closest(string.Join(",", SkippedElements)) is null)
            {
                foreach (var a in el.Attributes)
                {
                    var userFacing = a.Name is "placeholder" or "title" or "aria-label" or "alt" or "data-confirm"
                        || a.Name.StartsWith("data-help-", StringComparison.Ordinal) || a.Name.StartsWith("data-placeholder-", StringComparison.Ordinal)
                        || (a.Name == "value" && el.LocalName == "input" && el.GetAttribute("type") is "submit" or "button");
                    if (userFacing) Check(a.Value, $"{el.LocalName}[{a.Name}]");
                }
            }
        }
        return found.Distinct().ToList();
    }

    internal static async Task SweepAsync(HttpClient client, IEnumerable<string> urls)
    {
        var failures = new List<string>();
        foreach (var url in urls)
        {
            var r = await client.GetAsync(url);
            if (r.StatusCode != HttpStatusCode.OK) { failures.Add($"{url}: HTTP {(int)r.StatusCode}"); continue; }
            var html = await r.Content.ReadAsStringAsync();
            if (!html.Contains("<html lang=\"tr\">")) { failures.Add($"{url}: not rendered in Turkish"); continue; }
            failures.AddRange(FindEnglish(html).Select(e => $"{url}: {e}"));
        }
        Assert.True(failures.Count == 0, "English text in Turkish pages:\n" + string.Join("\n", failures));
    }

    private async Task<HttpClient> TurkishAsync(Func<Task<HttpClient>>? login = null)
    {
        var c = login is null ? f.CreateBrowser() : await login();
        await c.SetLanguageAsync("tr");
        return c;
    }

    [Fact]
    public void Detector_flags_English_and_passes_Turkish()
    {
        Assert.NotEmpty(FindEnglish("<html lang=\"tr\"><body><p>Save</p></body></html>"));
        Assert.NotEmpty(FindEnglish("<html lang=\"tr\"><body><input placeholder=\"Search your monitors\"></body></html>"));
        Assert.NotEmpty(FindEnglish("<html lang=\"tr\"><body><button title=\"Delete the monitor\">x</button></body></html>"));
        Assert.Empty(FindEnglish("<html lang=\"tr\"><body><p>Kaydet</p><code>the curl</code><p>https://the.example/and</p></body></html>"));
    }

    [Fact]
    public async Task Anonymous_pages_have_no_English_text()
    {
        var t = await TenantAsync();
        await SweepAsync(await TurkishAsync(), [
            "/", "/Features", "/Pricing", "/Account/Login", "/Account/Register", "/Account/ForgotPassword",
            $"/status/{t.Slug}", $"/status/{t.Slug}/subscribe"
        ]);
    }

    [Fact]
    public async Task Customer_panel_pages_have_no_English_text()
    {
        var t = await TenantAsync();
        await SweepAsync(await TurkishAsync(() => f.LoginAsync(t.Admin, Password)), [
            "/App", "/App/Monitors", "/App/Monitors/Create", $"/App/Monitors/Details/{t.UpId}", $"/App/Monitors/Details/{t.DownId}",
            $"/App/Monitors/Edit/{t.UpId}", $"/App/Monitors/Delete/{t.UpId}", $"/App/Monitors/Regions/{t.UpId}",
            "/App/Incidents", $"/App/Incidents/Details/{t.IncidentId}", "/App/Incidents/Groups",
            "/App/Alerts", "/App/Alerts/Create", "/App/Maintenance", "/App/Maintenance/Create",
            $"/App/Maintenance/Edit/{t.MaintenanceId}", $"/App/Maintenance/Delete/{t.MaintenanceId}", "/App/Escalation",
            "/App/StatusPages", "/App/StatusPages/Create", $"/App/StatusPages/Edit/{t.StatusPageId}",
            $"/App/StatusPages/Notes/{t.StatusPageId}", $"/App/StatusPages/Domain/{t.StatusPageId}", $"/App/StatusPages/Delete/{t.StatusPageId}",
            "/App/Reports", "/App/Users", "/App/Users/Create", "/App/Billing", "/App/ApiKeys", "/App/Account", "/App/Ai", "/App/Ask",
            "/Account/ChangePassword"
        ]);
    }

    [Fact]
    public async Task SuperAdmin_pages_have_no_English_text()
    {
        var t = await TenantAsync();
        await SweepAsync(await TurkishAsync(f.LoginSuperAdminAsync), [
            "/SuperAdmin", "/SuperAdmin/Customers", "/SuperAdmin/Customers/Create", $"/SuperAdmin/Customers/Details/{t.CustomerId}",
            $"/SuperAdmin/Customers/Edit/{t.CustomerId}", "/SuperAdmin/Plans", "/SuperAdmin/Plans/Create", "/SuperAdmin/Monitors",
            $"/SuperAdmin/Customers/AddUser/{t.CustomerId}", $"/SuperAdmin/Customers/ApiKeys/{t.CustomerId}",
            $"/SuperAdmin/Plans/Edit/{await f.PlanIdAsync("Pro")}", "/SuperAdmin/Regions", "/SuperAdmin/Refunds", "/SuperAdmin/Ai"
        ]);
    }
}

/// <summary>UI-2: the AI pages with AI switched on (all features on the plan, customer opted in) have no English text in Turkish.</summary>
public class TurkishAiCoverageTests(AiEnabledWebAppFactory f) : IClassFixture<AiEnabledWebAppFactory>
{
    [Fact]
    public async Task Ai_pages_have_no_English_text()
    {
        var (cid, admin, _) = await f.AddTenantAsync("trai", "Enterprise");
        await f.WithDbAsync(async db =>
        {
            var c = await db.Customers.Include(x => x.Plan).SingleAsync(x => x.Id == cid);
            c.AiEnabled = true;
            c.AlertGroupingEnabled = true;
            c.Plan.AiFeatures = AiFeatures.Format(AiFeatures.All.Select(x => x.Name));
            c.Plan.AiMonthlyCallLimit = Math.Max(100, c.Plan.AiMonthlyCallLimit);
            await db.SaveChangesAsync();
        });
        var up = await f.AddMonitorAsync(cid, "Trai web", m => m.Status = MonitorStatus.Up);
        var incident = await f.AddIncidentAsync(up, DateTime.UtcNow.AddMinutes(-30), null, cause: "HTTP 503");

        var c = await f.LoginAsync(admin, Password);
        await c.SetLanguageAsync("tr");
        await TurkishCoverageTests.SweepAsync(c, [
            "/App", "/App/Ai", "/App/Ask", "/App/Incidents", "/App/Incidents/Groups", $"/App/Incidents/Details/{incident}",
            "/App/Monitors/Create", $"/App/Monitors/Details/{up}", "/App/Reports"
        ]);
    }
}
