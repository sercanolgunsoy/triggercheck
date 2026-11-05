using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Ai.Narrative;

/// <summary>What a narrative placeholder stands for; decides how the server renders it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NarrativeValueKind>))]
public enum NarrativeValueKind
{
    Percent,
    PercentagePoints,
    /// <summary>Minutes, rendered as "1 h 15 min" / "1 sa 15 dk".</summary>
    Duration,
    Count,
    Milliseconds,
    /// <summary>A calendar day (<see cref="NarrativeValue.At"/>).</summary>
    Date,
    /// <summary>Local date and time in the report time zone.</summary>
    DateTime,
    /// <summary>"met" / "missed" / "no_target" (<see cref="NarrativeValue.Text"/>).</summary>
    SlaStatus,
    /// <summary>Days from the end of the period to the certificate expiry (negative: already expired).</summary>
    CertificateExpiry,
    ChangeCount,
    ChangeDuration,
    ChangePercentagePoints,
    ChangeMilliseconds,
    ChangePercent,
    /// <summary>A period key ("2026-02" / "2026-W05").</summary>
    Period,
    /// <summary>A fixed error class code ("http_5xx", "timeout", ...).</summary>
    ErrorClass,
    StatusCode,
}

/// <summary>One value a narrative placeholder renders, computed from the database when the narrative was generated.</summary>
public sealed record NarrativeValue(NarrativeValueKind Kind, double? Number = null, string? Text = null, DateTime? At = null);

/// <summary>
/// Renders narrative placeholders ("{{A.uptime}}") from the stored values in the narrative's language: units, the
/// Turkish decimal comma, durations ("1 h 15 min" / "1 sa 15 dk"), directions of changes and SLA results are always
/// produced here, never by the model (AI-6: the model writes no numbers at all).
/// </summary>
public static partial class NarrativeRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    public static partial Regex Placeholder();

    /// <summary>Replaces every placeholder of <paramref name="text"/>; unknown ones are left as they are.</summary>
    public static string Render(string text, IReadOnlyDictionary<string, NarrativeValue>? values, string language) =>
        values is null ? text : Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Format(v, language) : m.Value);

    public static string Format(NarrativeValue v, string language)
    {
        var tr = language.StartsWith("tr", StringComparison.OrdinalIgnoreCase);
        var c = CultureInfo.GetCultureInfo(tr ? "tr-TR" : "en-US");
        string N(double x) => x.ToString("#,0.####", c);
        var n = v.Number ?? 0;
        var abs = Math.Abs(n);
        string Dir(string up, string down, string same) => n > 0 ? up : n < 0 ? down : same;
        return v.Kind switch
        {
            _ when v.Number is null && v.Kind is not (NarrativeValueKind.Date or NarrativeValueKind.DateTime or NarrativeValueKind.SlaStatus
                or NarrativeValueKind.Period or NarrativeValueKind.ErrorClass) => tr ? "bilinmiyor" : "n/a",
            NarrativeValueKind.Percent => tr ? "%" + N(n) : N(n) + "%",
            NarrativeValueKind.PercentagePoints => N(n) + (tr ? " puan" : " percentage points"),
            NarrativeValueKind.Duration => Duration(n, tr),
            NarrativeValueKind.Count => N(n),
            NarrativeValueKind.Milliseconds => N(n) + " ms",
            NarrativeValueKind.Date => v.At is DateTime d ? d.ToString(tr ? "d MMMM yyyy" : "d MMMM yyyy", c) : "?",
            NarrativeValueKind.DateTime => v.At is DateTime t ? t.ToString(tr ? "d MMMM yyyy HH:mm" : "d MMMM yyyy, HH:mm", c) : "?",
            NarrativeValueKind.SlaStatus => v.Text switch
            {
                "met" => tr ? "karşılandı" : "met",
                "missed" => tr ? "karşılanmadı" : "missed",
                _ => tr ? "SLA hedefi tanımlı değil" : "no SLA target set",
            },
            NarrativeValueKind.CertificateExpiry => n < 0
                ? (tr ? $"{N(abs)} gün önce sona erdi" : $"expired {N(abs)} day{(abs == 1 ? "" : "s")} before the end of the period")
                : n == 0 ? (tr ? "dönemin son gününde sona eriyor" : "expires at the end of the period")
                : (tr ? $"dönem sonundan {N(n)} gün sonra sona eriyor" : $"expires {N(n)} day{(n == 1 ? "" : "s")} after the end of the period"),
            NarrativeValueKind.ChangeCount => Dir(tr ? $"{N(abs)} artış" : $"{N(abs)} more", tr ? $"{N(abs)} azalış" : $"{N(abs)} fewer", tr ? "değişiklik yok" : "no change"),
            NarrativeValueKind.ChangeDuration => Dir(tr ? $"{Duration(abs, tr)} artış" : $"{Duration(abs, tr)} more",
                tr ? $"{Duration(abs, tr)} azalış" : $"{Duration(abs, tr)} less", tr ? "değişiklik yok" : "no change"),
            NarrativeValueKind.ChangePercentagePoints => Dir(tr ? $"{N(abs)} puan artış" : $"{N(abs)} percentage points higher",
                tr ? $"{N(abs)} puan düşüş" : $"{N(abs)} percentage points lower", tr ? "değişiklik yok" : "unchanged"),
            NarrativeValueKind.ChangeMilliseconds => Dir(tr ? $"{N(abs)} ms artış" : $"{N(abs)} ms higher",
                tr ? $"{N(abs)} ms azalış" : $"{N(abs)} ms lower", tr ? "değişiklik yok" : "unchanged"),
            NarrativeValueKind.ChangePercent => Dir(tr ? $"%{N(abs)} artış" : $"{N(abs)}% higher", tr ? $"%{N(abs)} azalış" : $"{N(abs)}% lower",
                tr ? "değişiklik yok" : "unchanged"),
            NarrativeValueKind.Period => PeriodName(v.Text, c, tr),
            NarrativeValueKind.ErrorClass => ErrorClassName(v.Text, tr),
            NarrativeValueKind.StatusCode => "HTTP " + N(n),
            _ => N(n),
        };
    }

    /// <summary>"1 h 15 min", "2 d 3 h", "45 s" / "1 sa 15 dk", "2 gün 3 sa", "45 sn" (rounded to whole minutes from one minute on).</summary>
    public static string Duration(double minutes, bool turkish)
    {
        if (minutes <= 0) return turkish ? "0 dk" : "0 min";
        if (minutes < 1)
        {
            var sec = (long)Math.Round(minutes * 60, MidpointRounding.AwayFromZero);
            return sec + (turkish ? " sn" : " s");
        }
        var total = (long)Math.Round(minutes, MidpointRounding.AwayFromZero);
        long d = total / 1440, h = total % 1440 / 60, m = total % 60;
        var parts = new List<string>();
        if (d > 0) parts.Add(d + (turkish ? " gün" : " d"));
        if (h > 0) parts.Add(h + (turkish ? " sa" : " h"));
        if (m > 0) parts.Add(m + (turkish ? " dk" : " min"));
        return string.Join(' ', parts);
    }

    private static string PeriodName(string? key, CultureInfo c, bool tr)
    {
        if (SlaReportService.TryParseWeekKey(key, out var y, out var w)) return tr ? $"{y} yılının {w}. haftası" : $"week {w} of {y}";
        return DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) ? m.ToString("MMMM yyyy", c) : key ?? "";
    }

    private static string ErrorClassName(string? code, bool tr) => code switch
    {
        "http_5xx" => tr ? "sunucu hataları (HTTP 5xx)" : "server errors (HTTP 5xx)",
        "http_4xx" => tr ? "istemci hataları (HTTP 4xx)" : "client errors (HTTP 4xx)",
        "http_status" => tr ? "beklenmeyen HTTP durum kodları" : "unexpected HTTP status codes",
        "timeout" => tr ? "zaman aşımları" : "timeouts",
        "dns" => tr ? "DNS hataları" : "DNS errors",
        "tls" => tr ? "sertifika / TLS hataları" : "certificate / TLS errors",
        "connection" => tr ? "bağlantı hataları" : "connection errors",
        "content_check" => tr ? "içerik kontrolü hataları" : "content check failures",
        "heartbeat_or_ping" => tr ? "kaçan heartbeat / ping" : "missed heartbeats / pings",
        _ => tr ? "diğer hatalar" : "other errors",
    };
}
