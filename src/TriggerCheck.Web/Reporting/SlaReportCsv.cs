using System.Globalization;
using System.Text;
using TriggerCheck.Domain.Reporting;

namespace TriggerCheck.Web.Reporting;

/// <summary>
/// CSV export of the SLA report: one row per monitor plus a "TOTAL" row; invariant culture (dot decimals), RFC 4180
/// quoting, UTF-8 with BOM (Excel). Cells that a spreadsheet would treat as a formula are prefixed with an apostrophe.
/// </summary>
public static class SlaReportCsv
{
    public static readonly string[] Header =
    [
        "month", "time_zone", "monitor", "type", "sla_target_percent", "uptime_percent", "sla_met", "downtime_minutes",
        "maintenance_minutes", "monitored_minutes", "incidents", "mttr_minutes", "avg_response_ms", "p95_response_ms", "checks"
    ];

    public static byte[] Render(SlaReport report)
    {
        var sb = new StringBuilder();
        Line(sb, Header);
        foreach (var m in report.Monitors)
            Line(sb, [report.MonthKey, report.TimeZoneId, m.Name, m.Type.ToString(), N(m.SlaTarget), N(m.UptimePercent),
                m.SlaMet switch { true => "yes", false => "no", _ => "" }, N(m.DowntimeMinutes), N(m.MaintenanceMinutes), N(m.MonitoredMinutes),
                N(m.IncidentCount), N(m.MttrMinutes), N(m.AvgResponseMs), N(m.P95ResponseMs), N(m.CheckCount)]);
        var s = report.Summary;
        Line(sb, [report.MonthKey, report.TimeZoneId, "TOTAL", "", "", N(s.UptimePercent), $"{s.SlaMet} met / {s.SlaMissed} missed",
            N(s.DowntimeMinutes), "", "", N(s.IncidentCount), N(s.MttrMinutes), N(s.AvgResponseMs), N(s.P95ResponseMs), ""]);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string N(double? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static void Line(StringBuilder sb, IEnumerable<string> cells) => sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");

    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
