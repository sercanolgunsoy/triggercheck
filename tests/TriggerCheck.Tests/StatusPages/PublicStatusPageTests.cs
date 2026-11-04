using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>FEAT-4: the anonymous public page, JSON and feed never leak private data and set no cookies.</summary>
public class PublicStatusPageTests(StatusPageTestFactory f) : IClassFixture<StatusPageTestFactory>
{
    private const string Secret = "secret-internal.example.net";

    /// <summary>Two shown monitors (one down with an open incident) and one private monitor of the same customer.</summary>
    private async Task<(int Cid, int Up, int Down, int Hidden, string Slug)> ArrangeAsync(string slug, Action<StatusPage>? configure = null)
    {
        var (cid, _, _) = await f.AddTenantAsync(slug);
        var up = await f.AddMonitorAsync(cid, "Internal API " + slug, m =>
        {
            m.Target = $"https://{Secret}/health"; m.Status = MonitorStatus.Up; m.CreatedAtUtc = Utc(2026, 1, 1);
        });
        var down = await f.AddMonitorAsync(cid, "prod-db-01 " + slug, m =>
        {
            m.Target = $"https://{Secret}/db"; m.Status = MonitorStatus.Down; m.LastError = "TLS handshake to 10.1.2.3 failed"; m.CreatedAtUtc = Utc(2026, 3, 30);
        });
        var hidden = await f.AddMonitorAsync(cid, "Hidden billing job " + slug, m => { m.Status = MonitorStatus.Down; m.CreatedAtUtc = Utc(2026, 1, 1); });
        await f.AddIncidentAsync(up, Utc(2026, 3, 31, 10), Utc(2026, 3, 31, 12), cause: "HTTP 500 from 10.1.2.3");
        await f.AddIncidentAsync(down, Utc(2026, 4, 2, 9), null, cause: "Connection refused by 10.1.2.3");
        await f.AddIncidentAsync(hidden, Utc(2026, 4, 1, 9), Utc(2026, 4, 1, 10), cause: "hidden cause");
        await f.AddStatusPageAsync(cid, slug, [up, down], configure);
        await f.EvictAsync();
        return (cid, up, down, hidden, slug);
    }

    private static void AssertNoLeak(string body, string slug)
    {
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("10.1.2.3", body);
        Assert.DoesNotContain("Internal API", body);
        Assert.DoesNotContain("prod-db-01", body);
        Assert.DoesNotContain("Hidden", body);
        Assert.DoesNotContain("Connection refused", body);
        Assert.DoesNotContain("HTTP 500", body);
        Assert.DoesNotContain("Tenant " + slug, body); // customer name
    }

    [Fact]
    public async Task Public_page_shows_display_names_and_status_without_leaks_or_cookies()
    {
        var (_, _, _, _, slug) = await ArrangeAsync("acme-public");
        var r = await f.CreateBrowser().GetAsync("/status/" + slug);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.ReadHtmlAsync();
        Assert.Contains("Title " + slug, html);
        Assert.Contains("Public 0", html);
        Assert.Contains("Public 1", html);
        Assert.Contains("Partial outage", html);
        Assert.Contains("Public 1 unavailable", html);
        Assert.Contains("Ongoing", html);
        AssertNoLeak(html, slug);

        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("__RequestVerificationToken", html);
        Assert.Contains("script-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(r.Headers.Contains("X-Robots-Tag"));
        Assert.Equal(90, System.Text.RegularExpressions.Regex.Matches(html, "<span class=\"[a-z]*\" title=\"2026-").Count / 2);
    }

    [Fact]
    public async Task Unknown_slug_and_suspended_customer_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/does-not-exist")).StatusCode);
        var (cid, _, _, _, slug) = await ArrangeAsync("acme-suspended");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.IsActive, false)));
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync("/status/" + slug)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CreateBrowser().GetAsync($"/status/{slug}.json")).StatusCode);
    }

    [Fact]
    public async Task Json_has_uptime_bars_incidents_and_notes_but_no_ids_or_targets()
    {
        var (_, up, _, _, slug) = await ArrangeAsync("acme-json");
        var incident = await f.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == up).Select(i => i.Id).FirstAsync());
        var pageId = await f.WithDbAsync(db => db.StatusPages.Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync());
        await f.WithDbAsync(async db =>
        {
            db.StatusPageNotes.Add(new StatusPageNote { StatusPageId = pageId, IncidentId = incident, Body = "We <b>fixed</b> it.", CreatedAtUtc = Utc(2026, 3, 31, 12) });
            await db.SaveChangesAsync();
        });

        var r = await f.CreateBrowser().GetAsync($"/status/{slug}.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        var body = await r.Content.ReadAsStringAsync();
        AssertNoLeak(body, slug);
        Assert.DoesNotContain("\"id\"", body);
        Assert.DoesNotContain("monitorId", body, StringComparison.OrdinalIgnoreCase);

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("partial_outage", json.GetProperty("status").GetString());
        var monitors = json.GetProperty("monitors");
        Assert.Equal(2, monitors.GetArrayLength());
        var first = monitors[0];
        Assert.Equal("Public 0", first.GetProperty("name").GetString());
        var days = first.GetProperty("days");
        Assert.Equal(90, days.GetArrayLength());
        var mar31 = days.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-03-31");
        Assert.Equal(120, mar31.GetProperty("downtimeMinutes").GetInt32());
        Assert.Equal(Math.Round(100.0 * 22 / 24, 3), mar31.GetProperty("uptimePercent").GetDouble());
        // The second monitor was created on 30 March: earlier days have no data.
        var second = monitors[1].GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, second.Single(d => d.GetProperty("date").GetString() == "2026-03-29").GetProperty("uptimePercent").ValueKind);
        Assert.Equal("major_outage", monitors[1].GetProperty("status").GetString());

        var incidents = json.GetProperty("incidents");
        Assert.Equal(2, incidents.GetArrayLength());
        var note = incidents.EnumerateArray().Single(i => i.GetProperty("monitor").GetString() == "Public 0").GetProperty("notes")[0];
        Assert.Equal("We <b>fixed</b> it.", note.GetProperty("body").GetString());

        // The note is HTML-encoded on the page.
        var html = await f.CreateBrowser().GetStringAsync("/status/" + slug);
        Assert.Contains("We &lt;b&gt;fixed&lt;/b&gt; it.", html);
    }

    [Fact]
    public async Task Feed_is_atom_without_private_data()
    {
        var (_, _, _, _, slug) = await ArrangeAsync("acme-feed");
        var r = await f.CreateBrowser().GetAsync($"/status/{slug}/feed");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/atom+xml", r.Content.Headers.ContentType?.MediaType);
        var xml = await r.Content.ReadAsStringAsync();
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal(2, doc.Root!.Elements().Count(e => e.Name.LocalName == "entry"));
        Assert.Contains("Public 1: ongoing outage", xml);
        AssertNoLeak(xml, slug);
    }

    [Fact]
    public async Task Only_maintenance_touching_page_monitors_is_shown()
    {
        var (cid, up, _, hidden, slug) = await ArrangeAsync("acme-maint");
        await f.AddMaintenanceAsync(cid, "Network switch swap", Utc(2026, 4, 2, 9), Utc(2026, 4, 2, 11), up);
        await f.AddMaintenanceAsync(cid, "Private batch window", Utc(2026, 4, 2, 9), Utc(2026, 4, 2, 11), hidden);
        await f.AddMaintenanceAsync(cid, "Upcoming storage work", Utc(2026, 4, 5, 1), Utc(2026, 4, 5, 2));
        await f.AddMaintenanceAsync(cid, "Far future", Utc(2026, 6, 1), Utc(2026, 6, 2));
        await f.EvictAsync();

        var html = await f.CreateBrowser().GetHtmlAsync("/status/" + slug);
        Assert.Contains("Network switch swap", html);
        Assert.Contains("Upcoming storage work", html);
        Assert.Contains("Scheduled maintenance", html);
        Assert.DoesNotContain("Private batch window", html);
        Assert.DoesNotContain("Far future", html);
        Assert.Contains("Maintenance in progress", html);
    }

    [Fact]
    public async Task Unlisted_pages_are_noindex_and_times_follow_the_page_zone()
    {
        var (_, _, _, _, slug) = await ArrangeAsync("acme-unlisted", p => { p.Visibility = StatusPageVisibility.Unlisted; p.TimeZoneId = "Europe/Istanbul"; });
        var r = await f.CreateBrowser().GetAsync("/status/" + slug);
        Assert.Equal("noindex, nofollow", r.Headers.GetValues("X-Robots-Tag").Single());
        var html = await r.ReadHtmlAsync();
        Assert.Contains("<meta name=\"robots\" content=\"noindex, nofollow\" />", html);
        Assert.Contains("2026-04-02 12:00", html); // incident start 09:00Z in UTC+3
    }

    [Fact]
    public async Task Turkish_visitors_get_the_turkish_page()
    {
        var (_, _, _, _, slug) = await ArrangeAsync("acme-tr");
        var c = f.CreateBrowser();
        await c.SetLanguageAsync("tr");
        Assert.Contains("Kısmi kesinti", await c.GetHtmlAsync("/status/" + slug));
    }
}
