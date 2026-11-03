using System.Globalization;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>
/// Display helpers for the customer panel. All timestamps are stored in UTC and shown in Europe/Istanbul time
/// (labelled "TSİ") consistently across the panel.
/// </summary>
public static class AppFormat
{
    public const string TimeZoneLabel = "TSİ";

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = ResolveZone();

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Europe/Istanbul", "Turkey Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TRT", "TRT");
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Istanbul);

    /// <summary>"03.11.2026 14:05:09"</summary>
    public static string Time(DateTime? utc) =>
        utc is DateTime d ? ToLocal(d).ToString("dd.MM.yyyy HH:mm:ss", Tr) : "-";

    /// <summary>"14:05" — used for chart labels.</summary>
    public static string ShortTime(DateTime utc) => ToLocal(utc).ToString("HH:mm", Tr);

    /// <summary>Turkish human readable duration: "45 sn", "12 dk", "1 sa 5 dk", "2 gün 3 sa".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} sn";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk";
        if (span.TotalDays < 1) return span.Minutes > 0 ? $"{(int)span.TotalHours} sa {span.Minutes} dk" : $"{(int)span.TotalHours} sa";
        return span.Hours > 0 ? $"{(int)span.TotalDays} gün {span.Hours} sa" : $"{(int)span.TotalDays} gün";
    }

    /// <summary>"%99,95" or "-" when there is no data.</summary>
    public static string Percent(double? value) =>
        value is double v ? "%" + v.ToString(v >= 99.995 && v < 100 ? "0.000" : "0.##", Tr) : "-";

    public static string Ms(int? ms) => ms is int v ? $"{v} ms" : "-";

    public static string TypeName(Domain.Entities.MonitorType t) => t switch
    {
        Domain.Entities.MonitorType.Http => "HTTP(S)",
        Domain.Entities.MonitorType.Keyword => "Anahtar kelime",
        Domain.Entities.MonitorType.Tcp => "TCP port",
        Domain.Entities.MonitorType.Ping => "Ping",
        _ => t.ToString()
    };

    public static string RoleName(string? role) => role switch
    {
        Domain.Roles.CustomerAdmin => "Yönetici",
        Domain.Roles.CustomerViewer => "Görüntüleyici",
        _ => role ?? "-"
    };
}
