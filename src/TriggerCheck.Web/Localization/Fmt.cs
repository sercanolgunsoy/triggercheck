using System.Globalization;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Web.Localization;

/// <summary>
/// Culture-aware display helpers shared by both panels. Times are stored in UTC and shown in Europe/Istanbul time.
/// Numbers and dates follow the UI culture (the request's formatting culture stays invariant so that model
/// binding parses "9.90" the same way on every host).
/// </summary>
public static class Fmt
{
    private static IStringLocalizer? _l;

    /// <summary>Called once at startup. Without it (plain unit tests) the English keys are returned.</summary>
    public static void Configure(IStringLocalizerFactory factory) => _l = factory.Create(typeof(SharedResource));

    private static string T(string key) => _l?[key].Value ?? key;
    private static string T(string key, params object[] args) =>
        _l?[key, args].Value ?? string.Format(CultureInfo.InvariantCulture, key, args);

    /// <summary>The UI culture, used as the formatting culture for display.</summary>
    public static CultureInfo Culture => CultureInfo.CurrentUICulture;
    public static bool IsTurkish => Culture.TwoLetterISOLanguageName == "tr";

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

    /// <summary>"TRT" / "TSİ".</summary>
    public static string TimeZoneLabel => T("TRT");

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Istanbul);

    /// <summary>en "2026-11-03 14:05:09", tr "03.11.2026 14:05:09"; null → "—".</summary>
    public static string Time(DateTime? utc) =>
        utc is DateTime d ? ToLocal(d).ToString(IsTurkish ? "dd.MM.yyyy HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    /// <summary>en "2026-11-03", tr "03.11.2026"; null → "—".</summary>
    public static string Date(DateTime? utc) =>
        utc is DateTime d ? ToLocal(d).ToString(IsTurkish ? "dd.MM.yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    /// <summary>"14:05" (chart labels).</summary>
    public static string ShortTime(DateTime utc) => ToLocal(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>en "45 sec", "12 min", "1 h 5 min", "2 d 3 h"; tr "45 sn", "12 dk", "1 sa 5 dk", "2 gün 3 sa".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return T("{0} sec", (int)span.TotalSeconds);
        if (span.TotalHours < 1) return T("{0} min", (int)span.TotalMinutes);
        if (span.TotalDays < 1)
            return span.Minutes > 0 ? T("{0} h {1} min", (int)span.TotalHours, span.Minutes) : T("{0} h", (int)span.TotalHours);
        return span.Hours > 0 ? T("{0} d {1} h", (int)span.TotalDays, span.Hours) : T("{0} d", (int)span.TotalDays);
    }

    /// <summary>Check interval: en "5 min" / "30 sec", tr "5 dk" / "30 sn".</summary>
    public static string Interval(int seconds) =>
        seconds >= 60 && seconds % 60 == 0 ? T("{0} min", seconds / 60) : T("{0} sec", seconds);

    /// <summary>en "99.95%", tr "%99,95"; null → "—".</summary>
    public static string Percent(double? value)
    {
        if (value is not double v) return "—";
        var n = v.ToString(v >= 99.995 && v < 100 ? "0.000" : "0.##", Culture);
        return IsTurkish ? "%" + n : n + "%";
    }

    /// <summary>Thousands-separated integer: en "1,234", tr "1.234".</summary>
    public static string Number(long n) => n.ToString("N0", Culture);

    /// <summary>en "$15.00", tr "15,00 $".</summary>
    public static string Money(decimal v) => IsTurkish ? v.ToString("N2", Culture) + " $" : "$" + v.ToString("N2", Culture);

    public static string Ms(int? ms) => ms is int v ? $"{v} ms" : "—";

    public static string TypeName(MonitorType t) => t switch
    {
        MonitorType.Http => "HTTP(S)",
        MonitorType.Keyword => T("Keyword"),
        MonitorType.Tcp => T("TCP port"),
        MonitorType.Ping => "Ping",
        _ => t.ToString()
    };

    public static string StatusName(MonitorStatus s) => s switch
    {
        MonitorStatus.Up => T("UP"),
        MonitorStatus.Down => T("DOWN"),
        MonitorStatus.Paused => T("PAUSED"),
        _ => T("PENDING")
    };

    public static string RoleName(string? role) => role switch
    {
        Roles.SuperAdmin => "SuperAdmin",
        Roles.CustomerAdmin => T("Admin"),
        Roles.CustomerViewer => T("Viewer"),
        _ => role ?? "—"
    };
}
