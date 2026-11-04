using Microsoft.EntityFrameworkCore;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Reporting;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Reporting;

/// <summary>Registration of the SLA reports (FEAT-10): report builder, PDF, signed links, the monthly e-mail job.</summary>
public static class ReportingSetup
{
    public static IServiceCollection AddReporting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ReportOptions>(config.GetSection(ReportOptions.Section));
        services.AddScoped<SlaReportService>();
        services.AddScoped<SlaReportCache>();
        services.AddMemoryCache();
        services.AddScoped<SlaReportPdf>();
        services.AddSingleton<ReportLinks>();
        services.AddScoped<MonthlyReportMailer>();
        services.AddHostedService<MonthlyReportService>();
        return services;
    }

    /// <summary>File name of a report download, e.g. "sla-report-2026-10.pdf".</summary>
    public static string FileName(string monthKey, string extension) => $"sla-report-{monthKey}.{extension}";

    /// <summary>
    /// <c>GET /reports/download?token=...</c>: the PDF of an e-mailed report (signed, expiring link; see
    /// <see cref="ReportLinks"/>). Anonymous on purpose (opened from the mail client), rate-limited per client, never cached.
    /// </summary>
    public static void MapReportEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet(ReportLinks.DownloadPath, async (string? token, HttpContext http, ReportLinks links, AppDbContext db,
            SlaReportCache reports, SlaReportPdf pdf) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (!links.TryRead(token, out var t)) return Results.NotFound();
            if (!await db.Customers.AnyAsync(c => c.Id == t!.CustomerId && c.IsActive, http.RequestAborted)) return Results.NotFound();
            var report = await reports.GetAsync(t!.CustomerId, t.Year, t.Month, t.TimeZoneId, http.RequestAborted);
            if (report is null) return Results.NotFound();
            return Results.File(pdf.Render(report), "application/pdf", FileName(report.MonthKey, "pdf"));
        }).AllowAnonymous().RequireRateLimiting(StatusPageSetup.ReportDownloadPolicy).ExcludeFromDescription();
}
