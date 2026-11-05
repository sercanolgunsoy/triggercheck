using System.Net;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Tests.StatusPages;
using TriggerCheck.Tests.SuperAdmin;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.Accessibility;

/// <summary>UI-3: key pages of every layout pass the static accessibility audit, in English and Turkish.</summary>
public class AccessibilityPageTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private sealed record Tenant(int CustomerId, string Admin, int UpId, int DownId, int IncidentId, int MaintenanceId, int StatusPageId, string Slug, int ResolvedIncidentId);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Tenant? _tenant;

    /// <summary>One Pro tenant with up / down / paused monitors, checks, incidents, a maintenance window and a status page.</summary>
    private async Task<Tenant> TenantAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_tenant is not null) return _tenant;
            var (cid, admin, _) = await f.AddTenantAsync("a11y");
            var now = f.Now;
            var up = await f.AddMonitorAsync(cid, "A11y web", m => { m.Status = MonitorStatus.Up; m.CreatedAtUtc = now.AddDays(-10); });
            var down = await f.AddMonitorAsync(cid, "A11y api", m => { m.Status = MonitorStatus.Down; m.LastError = "HTTP 503"; m.CreatedAtUtc = now.AddDays(-10); });
            await f.AddMonitorAsync(cid, "A11y paused", m => m.Status = MonitorStatus.Paused);
            await f.AddMonitorAsync(cid, "A11y pending", m => m.Status = MonitorStatus.Unknown);
            for (var i = 0; i < 30; i++)
            {
                await f.AddCheckAsync(up, now.AddMinutes(-10 * i), up: true, ms: 120 + i);
                await f.AddCheckAsync(down, now.AddMinutes(-10 * i), up: i > 3, ms: 300);
            }
            var resolved = await f.AddIncidentAsync(up, now.AddDays(-2), now.AddDays(-2).AddMinutes(20));
            var incident = await f.AddIncidentAsync(down, now.AddMinutes(-35), null);
            var window = await f.AddMaintenanceAsync(cid, "A11y window", now.AddDays(1), now.AddDays(1).AddHours(2), up);
            var page = await f.AddStatusPageAsync(cid, "a11y", [up, down], p => p.Announcement = "Planned work on Friday");
            await f.EvictAsync();
            return _tenant = new Tenant(cid, admin, up, down, incident, window, page, "a11y", resolved);
        }
        finally { Gate.Release(); }
    }

    private async Task AuditAsync(HttpClient client, string url, string lang)
    {
        var r = await client.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url}: HTTP {(int)r.StatusCode}");
        var problems = AccessibilityAudit.Check(await r.Content.ReadAsStringAsync(), lang);
        Assert.True(problems.Count == 0, $"{url} ({lang}):\n  " + string.Join("\n  ", problems));
    }

    private async Task AuditAllAsync(HttpClient client, IEnumerable<string> urls, string lang)
    {
        var failures = new List<string>();
        foreach (var url in urls)
        {
            try { await AuditAsync(client, url, lang); }
            catch (Exception e) when (e is Xunit.Sdk.XunitException) { failures.Add(e.Message); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private async Task<HttpClient> ClientAsync(string lang, Func<Task<HttpClient>>? login = null)
    {
        var c = login is null ? f.CreateBrowser() : await login();
        if (lang != "en") await c.SetLanguageAsync(lang);
        return c;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public async Task Anonymous_pages_pass(string lang)
    {
        var t = await TenantAsync();
        await AuditAllAsync(await ClientAsync(lang), [
            "/", "/Features", "/Pricing", "/Account/Login", "/Account/Register", "/Account/ForgotPassword",
            $"/status/{t.Slug}", $"/status/{t.Slug}/subscribe"
        ], lang);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public async Task Customer_panel_pages_pass(string lang)
    {
        var t = await TenantAsync();
        var c = await ClientAsync(lang, () => f.LoginAsync(t.Admin, Password));
        await AuditAllAsync(c, [
            "/App", "/App/Monitors", "/App/Monitors/Create", $"/App/Monitors/Details/{t.UpId}", $"/App/Monitors/Details/{t.DownId}",
            $"/App/Monitors/Edit/{t.UpId}", "/App/Incidents", $"/App/Incidents/Details/{t.IncidentId}", $"/App/Incidents/Postmortem/{t.ResolvedIncidentId}", "/App/Alerts", "/App/Alerts/Create",
            "/App/Maintenance", "/App/Maintenance/Create", "/App/Escalation", "/App/StatusPages", "/App/StatusPages/Create",
            $"/App/Monitors/Regions/{t.UpId}", $"/App/Maintenance/Edit/{t.MaintenanceId}", $"/App/StatusPages/Edit/{t.StatusPageId}",
            $"/App/StatusPages/Notes/{t.StatusPageId}", $"/App/StatusPages/Domain/{t.StatusPageId}",
            "/App/Reports", "/App/Users", "/App/Users/Create", "/App/Billing", "/App/ApiKeys", "/App/Account", "/App/Ai",
            "/Account/ChangePassword"
        ], lang);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public async Task SuperAdmin_pages_pass(string lang)
    {
        var t = await TenantAsync();
        var c = await ClientAsync(lang, f.LoginSuperAdminAsync);
        await AuditAllAsync(c, [
            "/SuperAdmin", "/SuperAdmin/Customers", "/SuperAdmin/Customers/Create", $"/SuperAdmin/Customers/Details/{t.CustomerId}",
            $"/SuperAdmin/Customers/Edit/{t.CustomerId}", "/SuperAdmin/Plans", "/SuperAdmin/Plans/Create", "/SuperAdmin/Monitors",
            $"/SuperAdmin/Customers/AddUser/{t.CustomerId}", $"/SuperAdmin/Customers/ApiKeys/{t.CustomerId}",
            $"/SuperAdmin/Plans/Edit/{await f.PlanIdAsync("Pro")}", "/SuperAdmin/Regions", "/SuperAdmin/Refunds", "/SuperAdmin/Ai"
        ], lang);
    }

    [Fact]
    public async Task Server_side_validation_errors_are_linked_to_their_fields()
    {
        var t = await TenantAsync();
        var c = await f.LoginAsync(t.Admin, Password);
        var r = await c.PostFormAsync("/App/Monitors/Create", AppTestData.MonitorForm("", ("Target", "")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Empty(AccessibilityAudit.Check(html, "en"));

        var doc = AccessibilityAudit.Parse(html);
        var name = doc.GetElementById("Input_Name")!;
        Assert.Equal("true", name.GetAttribute("aria-invalid"));
        var error = doc.GetElementById(name.GetAttribute("aria-describedby")!.Split(' ').Last())!;
        Assert.Contains("field-validation-error", error.ClassName);
        Assert.False(string.IsNullOrWhiteSpace(error.TextContent));
    }

    [Fact]
    public async Task Status_badges_carry_a_text_label()
    {
        var t = await TenantAsync();
        var c = await f.LoginAsync(t.Admin, Password);
        var doc = AccessibilityAudit.Parse(await c.GetStringAsync("/App/Monitors"));
        var badges = doc.QuerySelectorAll(".status");
        Assert.NotEmpty(badges);
        Assert.All(badges, b => Assert.False(string.IsNullOrWhiteSpace(b.TextContent), b.OuterHtml));
        foreach (var label in new[] { "UP", "DOWN", "PAUSED", "PENDING" })
            Assert.Contains(badges, b => b.TextContent.Trim() == label);

        var status = AccessibilityAudit.Parse(await f.CreateBrowser().GetStringAsync($"/status/{t.Slug}"));
        // Every uptime bar has a text alternative, and each monitor row states its status in words.
        Assert.All(status.QuerySelectorAll(".uptime-bars"), b => Assert.False(string.IsNullOrWhiteSpace(b.GetAttribute("aria-label"))));
        Assert.All(status.QuerySelectorAll(".status-monitor"), m => Assert.NotNull(m.QuerySelector("[class^=state-]")));
    }
}
