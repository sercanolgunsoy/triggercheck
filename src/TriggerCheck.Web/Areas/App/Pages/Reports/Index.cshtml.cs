using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Monitoring.Reporting;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.Reporting;

namespace TriggerCheck.Web.Areas.App.Pages.Reports;

/// <summary>
/// FEAT-10: monthly SLA report. Every member (viewers too) can view and download it (PDF / CSV) - it only aggregates
/// data they can already see; the monthly e-mail setting is CustomerAdmin only.
/// </summary>
public class IndexModel(AppDbContext db, ICurrentCustomer current, SlaReportService reports, SlaReportPdf pdf, TimeProvider time,
    Text<AppResource> L) : PageModel
{
    public const int MonthsOffered = 12;

    public SlaReport Report { get; private set; } = null!;
    public string Month { get; private set; } = "";
    public List<string> Months { get; private set; } = [];
    public bool MonthlyEmail { get; private set; }
    public string TimeZoneId { get; private set; } = SlaReportService.DefaultTimeZone;

    public async Task<IActionResult> OnGetAsync(string? month, CancellationToken ct) =>
        await LoadAsync(month, ct) ? Page() : NotFound();

    public async Task<IActionResult> OnGetPdfAsync(string? month, CancellationToken ct)
    {
        if (!await LoadAsync(month, ct)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(pdf.Render(Report), "application/pdf", ReportingSetup.FileName(Report.MonthKey, "pdf"));
    }

    public async Task<IActionResult> OnGetCsvAsync(string? month, CancellationToken ct)
    {
        if (!await LoadAsync(month, ct)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(SlaReportCsv.Render(Report), "text/csv; charset=utf-8", ReportingSetup.FileName(Report.MonthKey, "csv"));
    }

    /// <summary>CustomerAdmin: monthly e-mail opt-in and the report time zone.</summary>
    public async Task<IActionResult> OnPostSettingsAsync(bool monthlyEmail, string? timeZoneId, string? month, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var zone = string.IsNullOrWhiteSpace(timeZoneId) ? SlaReportService.DefaultTimeZone : timeZoneId.Trim();
        if (MaintenanceSchedule.FindTimeZone(zone) is null)
        {
            TempData["Error"] = L["Unknown time zone."].Value;
            return RedirectToPage(new { month });
        }
        var cid = current.RequiredCustomerId;
        await db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.MonthlyReportEmail, monthlyEmail)
            .SetProperty(c => c.ReportTimeZoneId, zone == SlaReportService.DefaultTimeZone ? null : zone), ct);
        TempData["Success"] = L["Report settings saved."].Value;
        return RedirectToPage(new { month });
    }

    /// <summary>Parses "yyyy-MM" (default: the previous month in the report time zone). False for invalid input.</summary>
    private async Task<bool> LoadAsync(string? month, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var customer = await current.GetCustomerAsync(ct);
        MonthlyEmail = customer.MonthlyReportEmail;
        TimeZoneId = customer.ReportTimeZoneId ?? SlaReportService.DefaultTimeZone;
        var tz = SlaReportService.ResolveZone(TimeZoneId);
        var local = MaintenanceSchedule.UtcToLocal(time.GetUtcNow().UtcDateTime, tz);
        var thisMonth = new DateTime(local.Year, local.Month, 1);
        Months = Enumerable.Range(0, MonthsOffered).Select(i => thisMonth.AddMonths(-i).ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToList();

        DateTime selected;
        if (string.IsNullOrWhiteSpace(month)) selected = thisMonth.AddMonths(-1);
        else if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out selected)
                 || selected.Year < 2000 || selected > thisMonth) return false;
        Month = selected.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!Months.Contains(Month)) Months.Add(Month);
        Report = (await reports.BuildAsync(cid, selected.Year, selected.Month, TimeZoneId, ct))!;
        return true;
    }
}
