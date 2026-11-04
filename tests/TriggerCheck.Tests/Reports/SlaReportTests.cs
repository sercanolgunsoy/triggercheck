using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Monitoring.Reporting;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Reporting;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.Reports;

/// <summary>FEAT-10: monthly SLA report (math against the database, exports, page access, monthly e-mail).</summary>
public class SlaReportTests(ReportsTestFactory f) : IClassFixture<ReportsTestFactory>
{
    /// <summary>
    /// Customer in Europe/Istanbul (UTC+3, no DST): March 2026 is [2026-02-28 21:00Z, 2026-03-31 21:00Z).
    /// Monitor A (target 99.9): incident spanning the month start (60 min inside), a 60 min incident half covered by a
    /// 2 h maintenance window, and an incident starting 30 min before the month end. Monitor B (target 99) is created
    /// mid-month and has no incidents.
    /// </summary>
    private async Task<(int Cid, int A, int B)> ArrangeAsync(string tag)
    {
        var (cid, _, _) = await f.AddTenantAsync(tag);
        var a = await f.AddMonitorAsync(cid, "A " + tag, m => { m.CreatedAtUtc = Utc(2026, 1, 1); m.SlaTarget = 99.9; });
        var b = await f.AddMonitorAsync(cid, "B " + tag, m => { m.CreatedAtUtc = Utc(2026, 3, 16, 21); m.SlaTarget = 99; });
        await f.AddIncidentAsync(a, Utc(2026, 2, 28, 20), Utc(2026, 2, 28, 22));
        await f.AddIncidentAsync(a, Utc(2026, 3, 10, 0), Utc(2026, 3, 10, 1));
        await f.AddIncidentAsync(a, Utc(2026, 3, 31, 20, 30), Utc(2026, 4, 1, 0));
        await f.AddMaintenanceAsync(cid, "DB upgrade", Utc(2026, 3, 10, 0, 30), Utc(2026, 3, 10, 2, 30), a);
        for (var i = 1; i <= 20; i++) await f.AddCheckAsync(a, Utc(2026, 3, 5, 0, i), up: true, ms: i * 10);
        await f.AddCheckAsync(a, Utc(2026, 3, 10, 1), up: true, ms: 9999, inMaintenance: true);
        await f.AddCheckAsync(a, Utc(2026, 3, 6), up: true, ms: 9999, confirmation: true);
        await f.AddCheckAsync(a, Utc(2026, 3, 7), up: false, ms: 5000);
        await f.AddCheckAsync(a, Utc(2026, 3, 31, 22), up: true, ms: 9999); // April in Istanbul, March in UTC
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.ReportTimeZoneId, "Europe/Istanbul")));
        return (cid, a, b);
    }

    private async Task<SlaReport> BuildAsync(int cid, int year, int month, string? tz)
    {
        using var scope = f.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SlaReportService>().BuildAsync(cid, year, month, tz))!;
    }

    [Fact]
    public async Task Report_math_uses_the_time_zone_month_and_excludes_maintenance()
    {
        var (cid, a, b) = await ArrangeAsync("math");
        var r = await BuildAsync(cid, 2026, 3, "Europe/Istanbul");

        Assert.Equal(Utc(2026, 2, 28, 21), r.PeriodStartUtc);
        Assert.Equal(Utc(2026, 3, 31, 21), r.PeriodEndUtc);
        Assert.False(r.IsPartial);
        Assert.False(r.HistoryIncomplete);

        var ma = r.Monitors.Single(m => m.MonitorId == a);
        Assert.Equal(31 * 24 * 60, ma.MonitoredMinutes);
        Assert.Equal(120, ma.MaintenanceMinutes);
        Assert.Equal(60 + 30 + 30, ma.DowntimeMinutes); // boundary-spanning 60, maintenance-cut 30, month-end 30
        Assert.Equal(Math.Round(100.0 * (1 - 120.0 / (31 * 24 * 60 - 120)), 4), ma.UptimePercent);
        Assert.Equal(2, ma.IncidentCount); // the one that started in February is not counted
        Assert.Equal((60 + 210) / 2.0, ma.MttrMinutes); // full durations
        Assert.False(ma.SlaMet);
        Assert.Equal(20, ma.CheckCount);
        Assert.Equal(105, ma.AvgResponseMs);
        Assert.Equal(190, ma.P95ResponseMs);

        var mb = r.Monitors.Single(m => m.MonitorId == b);
        Assert.Equal(15 * 24 * 60, mb.MonitoredMinutes); // created at local midnight of the 17th
        Assert.Equal(100, mb.UptimePercent);
        Assert.True(mb.SlaMet);
        Assert.Null(mb.P95ResponseMs);

        Assert.Equal(2, r.Summary.IncidentCount);
        Assert.Equal(1, r.Summary.SlaMet);
        Assert.Equal(1, r.Summary.SlaMissed);
        Assert.Equal(120, r.Summary.DowntimeMinutes);
        Assert.Equal(190, r.Summary.P95ResponseMs);
    }

    [Fact]
    public async Task Utc_month_boundaries_differ_from_local_ones()
    {
        var (cid, a, _) = await ArrangeAsync("utc");
        var r = await BuildAsync(cid, 2026, 3, null);
        Assert.Equal("UTC", r.TimeZoneId);
        Assert.Equal(Utc(2026, 3, 1), r.PeriodStartUtc);
        var ma = r.Monitors.Single(m => m.MonitorId == a);
        Assert.Equal(30 + 210, ma.DowntimeMinutes); // the February incident is outside, the month-end one fully inside
        Assert.Equal(21, ma.CheckCount); // the 31 March 22:00Z check belongs to March in UTC
    }

    [Fact]
    public async Task Running_month_is_partial_and_only_counts_until_now()
    {
        var (cid, a, _) = await ArrangeAsync("partial");
        var r = await BuildAsync(cid, 2026, 4, null);
        Assert.True(r.IsPartial);
        Assert.Equal(f.Now, r.PeriodEndUtc);
        Assert.Equal((f.Now - Utc(2026, 4, 1)).TotalMinutes, r.Monitors.Single(m => m.MonitorId == a).MonitoredMinutes);
    }

    [Fact]
    public async Task Report_contains_only_the_customers_own_monitors()
    {
        var (cid, a, b) = await ArrangeAsync("iso1");
        var (other, _, _) = await ArrangeAsync("iso2");
        var r = await BuildAsync(cid, 2026, 3, null);
        Assert.Equal([a, b], r.Monitors.Select(m => m.MonitorId).Order());
        Assert.DoesNotContain(r.Monitors, m => m.Name.Contains("iso2"));
        Assert.NotEqual(cid, other);
    }

    [Fact]
    public async Task Pdf_and_csv_are_generated()
    {
        var (cid, _, _) = await ArrangeAsync("export");
        await f.AddMonitorAsync(cid, "=HYPERLINK(\"x\")", m => m.CreatedAtUtc = Utc(2026, 1, 1));
        await f.AddMonitorAsync(cid, "Şirket ağı İzmir", m => m.CreatedAtUtc = Utc(2026, 1, 1));
        var r = await BuildAsync(cid, 2026, 3, null);

        using var scope = f.Services.CreateScope();
        var pdf = scope.ServiceProvider.GetRequiredService<SlaReportPdf>().Render(r);
        Assert.True(pdf.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));

        var csv = Encoding.UTF8.GetString(SlaReportCsv.Render(r)).TrimStart('﻿');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(",", SlaReportCsv.Header), lines[0]);
        Assert.Equal(r.Monitors.Count + 2, lines.Length);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv); // formula neutralized and quoted
        Assert.Contains("Şirket ağı İzmir", csv);
        Assert.StartsWith("2026-03,UTC,TOTAL", lines[^1]);
    }

    [Fact]
    public async Task Viewer_can_view_and_download_but_not_change_settings()
    {
        var (cid, a, _) = await ArrangeAsync("viewer");
        var (_, otherAdmin, _) = await f.AddTenantAsync("viewer-other");
        var c = await f.LoginAsync("viewer-viewer@t.test", Password);

        var html = await c.GetHtmlAsync("/App/Reports?month=2026-03");
        Assert.Contains("A viewer", html);
        Assert.Contains($"data-monitor-id=\"{a}\"", html);
        Assert.DoesNotContain("name=\"monthlyEmail\"", html);

        var pdf = await c.GetAsync("/App/Reports?handler=Pdf&month=2026-03");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync(), 0, 4));
        var csv = await c.GetAsync("/App/Reports?handler=Csv&month=2026-03");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("A viewer", await csv.Content.ReadAsStringAsync());

        var post = await c.PostFormAsync("/App/Reports?handler=Settings", new() { ["monthlyEmail"] = "true", ["timeZoneId"] = "UTC" }, "/App/Reports");
        AppTestData.AssertAccessDenied(post);
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).Select(x => x.MonthlyReportEmail).FirstAsync()));

        // Another tenant sees only its own (empty) report.
        var other = await f.LoginAsync(otherAdmin, Password);
        Assert.DoesNotContain("A viewer", await other.GetHtmlAsync("/App/Reports?month=2026-03"));
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("garbage")]
    [InlineData("2026-05")] // future
    public async Task Invalid_months_are_not_found(string month)
    {
        await f.AddTenantAsync("bad" + month.Replace("-", ""));
        var c = await f.LoginAsync($"admin-bad{month.Replace("-", "")}@t.test", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/App/Reports?month=" + month)).StatusCode);
    }

    [Fact]
    public async Task Admin_saves_settings()
    {
        var (cid, admin, _) = await f.AddTenantAsync("settings");
        var c = await f.LoginAsync(admin, Password);
        var r = await c.PostFormAsync("/App/Reports?handler=Settings", new() { ["monthlyEmail"] = "true", ["timeZoneId"] = "Europe/Berlin" }, "/App/Reports");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var customer = await f.WithDbAsync(db => db.Customers.AsNoTracking().FirstAsync(x => x.Id == cid));
        Assert.True(customer.MonthlyReportEmail);
        Assert.Equal("Europe/Berlin", customer.ReportTimeZoneId);

        r = await c.PostFormAsync("/App/Reports?handler=Settings", new() { ["timeZoneId"] = "Mars/Olympus" }, "/App/Reports");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).Select(x => x.MonthlyReportEmail).FirstAsync()));
    }

    [Fact]
    public async Task Monthly_email_is_sent_once_per_customer_and_month_with_a_working_download_link()
    {
        var (cid, a, _) = await ArrangeAsync("mail");
        var (optedOut, _, _) = await ArrangeAsync("mail-off");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.MonthlyReportEmail, true)));

        for (var i = 0; i < 2; i++)
        {
            using var scope = f.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MonthlyReportMailer>().RunAsync(default);
        }

        var mails = await f.WithDbAsync(db => db.EmailOutbox.AsNoTracking().Where(m => m.To == "admin-mail@t.test").ToListAsync());
        var mail = Assert.Single(mails);
        Assert.Contains("2026-03", mail.Subject);
        Assert.Contains("Incidents: 2", mail.Body);
        Assert.Equal(1, await f.WithDbAsync(db => db.MonthlyReportDeliveries.CountAsync(d => d.CustomerId == cid && d.Month == "2026-03")));
        Assert.False(await f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == optedOut)));
        Assert.False(await f.WithDbAsync(db => db.EmailOutbox.AnyAsync(m => m.To == "viewer-mail@t.test")));

        var link = Regex.Match(mail.Body, @"https?://\S+/reports/download\?token=(\S+)");
        Assert.True(link.Success, mail.Body);
        var anon = f.CreateBrowser();
        var pdf = await anon.GetAsync("/reports/download?token=" + link.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.False(pdf.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/reports/download?token=" + link.Groups[1].Value[..^4] + "AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/reports/download?token=x")).StatusCode);
        _ = a;
    }

    [Fact]
    public async Task Monthly_email_is_not_sent_late_in_the_month()
    {
        var (cid, _, _) = await f.AddTenantAsync("late");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.MonthlyReportEmail, true).SetProperty(c => c.ReportTimeZoneId, "Pacific/Kiritimati")));
        // 2026-04-02 10:00Z is already the 3rd in Kiritimati (UTC+14) but still within the catch-up window.
        using var scope = f.Services.CreateScope();
        var mailer = scope.ServiceProvider.GetRequiredService<MonthlyReportMailer>();
        await mailer.RunAsync(default);
        Assert.True(await f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == cid && d.Month == "2026-03")));

        var (late, _, _) = await f.AddTenantAsync("late2");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == late).ExecuteUpdateAsync(u => u.SetProperty(c => c.MonthlyReportEmail, true)));
        var options = Microsoft.Extensions.Options.Options.Create(new ReportOptions { CatchUpDays = 1 });
        var strict = ActivatorUtilities.CreateInstance<MonthlyReportMailer>(scope.ServiceProvider,
            new StaticOptionsMonitor<ReportOptions>(options.Value));
        await strict.RunAsync(default); // the 2nd (UTC) is outside a 1-day window
        Assert.False(await f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == late)));
    }
}

/// <summary>Fixed options for a directly constructed service.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
