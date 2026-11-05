using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Reporting;

/// <summary>
/// PDF rendering of the SLA report with PDFsharp (MIT, fully managed). The Roboto font is embedded in the assembly
/// and resolved by <see cref="EmbeddedFontResolver"/>, so the output does not depend on fonts installed on the host
/// (the container image has none). Labels follow the current UI culture.
/// </summary>
public sealed class SlaReportPdf(Text<SharedResource> L)
{
    private const double Margin = 36;
    private const double RowHeight = 16;
    private static readonly object FontLock = new();

    /// <summary>Installs the embedded font resolver once per process.</summary>
    public static void EnsureFonts()
    {
        if (GlobalFontSettings.FontResolver is EmbeddedFontResolver) return;
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is not EmbeddedFontResolver)
                GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
        }
    }

    public byte[] Render(SlaReport report)
    {
        EnsureFonts();
        var doc = new PdfDocument();
        doc.Info.Title = L["SLA report {0}", report.MonthKey];
        doc.Info.Creator = "TriggerCheck";

        var title = new XFont(EmbeddedFontResolver.Family, 16, XFontStyleEx.Bold);
        var bold = new XFont(EmbeddedFontResolver.Family, 9, XFontStyleEx.Bold);
        var normal = new XFont(EmbeddedFontResolver.Family, 9, XFontStyleEx.Regular);
        var small = new XFont(EmbeddedFontResolver.Family, 8, XFontStyleEx.Regular);

        string[] headers =
        [
            L["Monitor"], L["SLA target"], L["Uptime"], L["SLA"], L["Downtime"], L["Maintenance"], L["Incidents"], L["MTTR"],
            L["Avg response"], L["p95 response"]
        ];
        double[] widths = [220, 60, 60, 50, 65, 70, 55, 55, 70, 65];

        PdfPage page = null!;
        XGraphics gfx = null!;
        double y = 0;
        void NewPage()
        {
            gfx?.Dispose();
            page = doc.AddPage();
            page.Size = PageSize.A4;
            page.Orientation = PageOrientation.Landscape;
            gfx = XGraphics.FromPdfPage(page);
            y = Margin;
        }
        void Text(string s, XFont f, double x, double w, bool right = false)
        {
            s = Fit(gfx, s, f, w - 4);
            gfx.DrawString(s, f, XBrushes.Black, new XRect(x + 2, y, w - 4, RowHeight), right ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
        }
        void Row(string[] cells, XFont f)
        {
            if (y + RowHeight > page.Height.Point - Margin) { NewPage(); Row(headers, bold); }
            var x = Margin;
            for (var i = 0; i < cells.Length; i++)
            {
                Text(cells[i], f, x, widths[i], right: i > 0);
                x += widths[i];
            }
            y += RowHeight;
            gfx.DrawLine(XPens.LightGray, Margin, y, x, y);
        }

        NewPage();
        gfx.DrawString(L["SLA report {0}", report.MonthKey], title, XBrushes.Black, new XPoint(Margin, y + 14));
        y += 24;
        Text(report.CustomerName, normal, Margin, 600);
        y += RowHeight;
        Text(L["Period: {0} to {1} ({2})", Local(report.PeriodStartUtc, report.TimeZoneId), Local(report.PeriodEndUtc, report.TimeZoneId), report.TimeZoneId], small, Margin, 760);
        y += RowHeight;
        if (report.IsPartial) { Text(L["The month is not over yet: figures up to {0}.", Local(report.GeneratedAtUtc, report.TimeZoneId)], small, Margin, 760); y += RowHeight; }
        if (report.HistoryIncomplete) { Text(L["Part of this month is older than your plan's data retention; figures may be incomplete."], small, Margin, 760); y += RowHeight; }
        y += 6;

        var s = report.Summary;
        string[] summaryLabels = [L["Uptime"], L["Downtime"], L["Incidents"], L["MTTR"], L["Avg response"], L["p95 response"], L["SLA met / missed"]];
        string[] summaryValues =
        [
            Fmt.Percent(s.UptimePercent), Minutes(s.DowntimeMinutes), s.IncidentCount.ToString(CultureInfo.InvariantCulture),
            Minutes(s.MttrMinutes), Ms(s.AvgResponseMs), Ms(s.P95ResponseMs), $"{s.SlaMet} / {s.SlaMissed}"
        ];
        var colW = 105.0;
        for (var i = 0; i < summaryLabels.Length; i++) Text(summaryLabels[i], small, Margin + i * colW, colW);
        y += RowHeight;
        for (var i = 0; i < summaryValues.Length; i++) Text(summaryValues[i], bold, Margin + i * colW, colW);
        y += RowHeight * 2;

        Row(headers, bold);
        foreach (var m in report.Monitors)
            Row([
                m.Name, m.SlaTarget is { } t ? Fmt.Percent(t) : "—", Fmt.Percent(m.UptimePercent),
                m.SlaMet switch { true => L["Met"], false => L["Missed"], _ => "—" },
                Minutes(m.DowntimeMinutes), Minutes(m.MaintenanceMinutes), m.IncidentCount.ToString(CultureInfo.InvariantCulture),
                Minutes(m.MttrMinutes), Ms(m.AvgResponseMs), Ms(m.P95ResponseMs)
            ], normal);
        if (report.Monitors.Count == 0) { Text(L["No monitors in this period."], normal, Margin, 600); y += RowHeight; }

        y += RowHeight;
        if (y + RowHeight > page.Height.Point - Margin) NewPage();
        Text(L["Uptime excludes maintenance windows. Downtime is the time monitors had an open incident; response times are of successful checks."], small, Margin, 760);
        gfx.Dispose();

        using var stream = new MemoryStream();
        doc.Save(stream, false);
        return stream.ToArray();
    }

    private static string Fit(XGraphics gfx, string s, XFont f, double width)
    {
        if (gfx.MeasureString(s, f).Width <= width) return s;
        while (s.Length > 1 && gfx.MeasureString(s + "…", f).Width > width) s = s[..^1];
        return s + "…";
    }

    private static string Local(DateTime utc, string zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Monitoring.Reporting.SlaReportService.ResolveZone(zone))
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Minutes(double? minutes) => minutes is { } m ? Fmt.Duration(TimeSpan.FromMinutes(m)) : "—";
    public static string Ms(double? ms) => ms is { } v ? Math.Round(v).ToString(CultureInfo.InvariantCulture) + " ms" : "—";
}

/// <summary>Serves the embedded Roboto regular / bold faces for every family name.</summary>
public sealed class EmbeddedFontResolver : IFontResolver
{
    public const string Family = "Roboto";
    private const string Regular = "Roboto-Regular";
    private const string Bold = "Roboto-Bold";

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? Bold : Regular, false, italic);

    public byte[]? GetFont(string faceName)
    {
        using var s = typeof(EmbeddedFontResolver).Assembly.GetManifestResourceStream($"TriggerCheck.Fonts.{faceName}.ttf");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
