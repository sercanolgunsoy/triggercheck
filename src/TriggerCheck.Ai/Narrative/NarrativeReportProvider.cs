using System.Globalization;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Grounding;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Ai.Narrative;

/// <summary>A report period: monthly "yyyy-MM" or weekly ISO "yyyy-Www" (the AI-6 subject id).</summary>
public sealed record NarrativePeriod(ReportPeriodKind Kind, string Key)
{
    public const string SubjectType = "SlaReport";

    public static bool TryParse(string? key, out NarrativePeriod period)
    {
        period = null!;
        if (SlaReportService.TryParseWeekKey(key, out _, out _))
        {
            period = new(ReportPeriodKind.Weekly, key!);
            return true;
        }
        if (key is { Length: 7 } && DateTime.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m) && m.Year >= 2000)
        {
            period = new(ReportPeriodKind.Monthly, key);
            return true;
        }
        return false;
    }

    public static NarrativePeriod Month(int year, int month) => new(ReportPeriodKind.Monthly, string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}"));

    /// <summary>The period before this one (previous month / week).</summary>
    public NarrativePeriod Previous()
    {
        if (Kind == ReportPeriodKind.Monthly)
        {
            var m = DateTime.ParseExact(Key, "yyyy-MM", CultureInfo.InvariantCulture).AddMonths(-1);
            return Month(m.Year, m.Month);
        }
        SlaReportService.TryParseWeekKey(Key, out var y, out var w);
        return new(ReportPeriodKind.Weekly, SlaReportService.WeekKey(ISOWeek.ToDateTime(y, w, DayOfWeek.Monday).AddDays(-7)));
    }

    /// <summary>UTC range of the period in <paramref name="tz"/>.</summary>
    public (DateTime StartUtc, DateTime EndUtc) Range(TimeZoneInfo tz)
    {
        if (Kind == ReportPeriodKind.Weekly)
        {
            SlaReportService.TryParseWeekKey(Key, out var y, out var w);
            return SlaReportService.WeekRange(y, w, tz);
        }
        var m = DateTime.ParseExact(Key, "yyyy-MM", CultureInfo.InvariantCulture);
        return SlaReportService.MonthRange(m.Year, m.Month, tz);
    }

    public Task<SlaReport?> BuildAsync(SlaReportService reports, int customerId, string timeZoneId, CancellationToken ct)
    {
        if (Kind == ReportPeriodKind.Weekly)
        {
            SlaReportService.TryParseWeekKey(Key, out var y, out var w);
            return reports.BuildWeekAsync(customerId, y, w, timeZoneId, ct);
        }
        var m = DateTime.ParseExact(Key, "yyyy-MM", CultureInfo.InvariantCulture);
        return reports.BuildAsync(customerId, m.Year, m.Month, timeZoneId, ct);
    }
}

/// <summary>
/// AI-6 context (subject = <see cref="NarrativePeriod"/> key of one customer). Every value is computed here from the
/// database (the FEAT-10 <see cref="SlaReportService"/> for this and the previous period, plus incidents, slowdown
/// signals and certificates through the tenant-scoped <see cref="AiTenantData"/>) and offered to the model as a
/// placeholder key ("A.uptime", "total.incidents", "change.downtime"); the model writes no numbers, the server renders
/// the values (<see cref="NarrativeRenderer"/>, contract <see cref="NarrativeOutputContract"/>). Monitors get neutral
/// labels ("monitor A"); names and targets are never sent (decision K5), nor the free-text incident cause (external
/// text: prompt injection, host names). No "now" in the content: an unchanged period gives the same input.
/// </summary>
public sealed class NarrativeReportProvider(AiTenantData data, SlaReportService reports) : IAiContextProvider
{
    public const int MaxMonitors = 20;

    public string Feature => AiFeatures.NarrativeReport;

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        if (job.SubjectType != NarrativePeriod.SubjectType || !NarrativePeriod.TryParse(job.SubjectId, out var period)) return null;
        // The period is cut in the zone the claim recorded (the customer's zone at that time).
        var tzId = (await data.NarrativeAsync(job.CustomerId, period.Kind, period.Key, ct))?.TimeZoneId
                   ?? await data.ReportTimeZoneAsync(job.CustomerId, ct);
        if (string.IsNullOrEmpty(tzId)) return null; // customer gone

        var report = await period.BuildAsync(reports, job.CustomerId, tzId, ct);
        if (report is null || report.IsPartial || report.Monitors.Count == 0) return null; // only complete periods with monitors
        var previous = await period.Previous().BuildAsync(reports, job.CustomerId, tzId, ct);
        var tz = SlaReportService.ResolveZone(tzId);

        var values = new Dictionary<string, NarrativeValue>(StringComparer.Ordinal);
        var b = new AiContextBuilder(job.Language);
        void Put(string key, NarrativeValue v)
        {
            values[key] = v;
            b.Identifier(key, NarrativeRenderer.Format(v, "en")); // shown as it will be rendered; the model writes {{key}}
        }
        NarrativeValue Pct(double? x) => new(NarrativeValueKind.Percent, x);
        NarrativeValue Dur(double? x) => new(NarrativeValueKind.Duration, x);
        NarrativeValue Cnt(double? x) => new(NarrativeValueKind.Count, x);
        NarrativeValue Ms(double? x) => new(NarrativeValueKind.Milliseconds, x);

        b.Section("Report period");
        Put("period.name", new(NarrativeValueKind.Period, Text: period.Key));
        Put("period.previous_name", new(NarrativeValueKind.Period, Text: period.Previous().Key));
        b.Fact("history_incomplete", report.HistoryIncomplete ? "yes (older than the data retention; figures may be incomplete)" : "no");

        void Summary(string title, string scope, SlaSummary s)
        {
            b.Section(title);
            Put(scope + ".monitors", Cnt(s.MonitorCount));
            Put(scope + ".uptime", Pct(s.UptimePercent));
            Put(scope + ".downtime", Dur(s.DowntimeMinutes));
            Put(scope + ".incidents", Cnt(s.IncidentCount));
            if (s.MttrMinutes is not null) Put(scope + ".mttr", Dur(s.MttrMinutes));
            Put(scope + ".avg_response", Ms(s.AvgResponseMs));
            Put(scope + ".p95_response", Ms(s.P95ResponseMs));
            Put(scope + ".sla_met", Cnt(s.SlaMet));
            Put(scope + ".sla_missed", Cnt(s.SlaMissed));
        }
        Summary("All monitors, this period (total.*)", "total", report.Summary);
        var prevMonitors = new Dictionary<int, MonitorSlaReport>();
        if (previous is { Monitors.Count: > 0 })
        {
            Summary("All monitors, previous period (prev.*)", "prev", previous.Summary);
            b.Section("Changes, this period minus the previous one (change.*; the value says the direction)");
            var (now, prev) = (report.Summary, previous.Summary);
            if (now.UptimePercent is double u && prev.UptimePercent is double pu)
                Put("change.uptime", new(NarrativeValueKind.ChangePercentagePoints, Math.Round(u - pu, 4)));
            Put("change.downtime", new(NarrativeValueKind.ChangeDuration, Math.Round(now.DowntimeMinutes - prev.DowntimeMinutes, 1)));
            Put("change.incidents", new(NarrativeValueKind.ChangeCount, now.IncidentCount - prev.IncidentCount));
            if (now.P95ResponseMs is int p && prev.P95ResponseMs is int pp)
            {
                Put("change.p95_response", new(NarrativeValueKind.ChangeMilliseconds, p - pp));
                if (pp > 0) Put("change.p95_response_percent", new(NarrativeValueKind.ChangePercent, Math.Round(100.0 * (p - pp) / pp, 1)));
            }
            if (now.AvgResponseMs is double avg && prev.AvgResponseMs is double pavg)
                Put("change.avg_response", new(NarrativeValueKind.ChangeMilliseconds, Math.Round(avg - pavg, 1)));
            Put("change.monitors", new(NarrativeValueKind.ChangeCount, now.MonitorCount - prev.MonitorCount));
            prevMonitors = previous.Monitors.ToDictionary(m => m.MonitorId);
        }
        else b.Section("Previous period").Fact("previous_period", "no data");

        // Most relevant monitors first: missed SLA, downtime, incidents, slowest p95.
        var listed = report.Monitors
            .OrderByDescending(m => m.SlaMet == false).ThenByDescending(m => m.DowntimeMinutes).ThenByDescending(m => m.IncidentCount)
            .ThenByDescending(m => m.P95ResponseMs ?? 0).ThenBy(m => m.MonitorId)
            .Take(MaxMonitors).ToList();
        var labels = new Dictionary<string, int>();
        for (var i = 0; i < listed.Count; i++) labels[NarrativeOutputContract.Label(i)] = listed[i].MonitorId;
        var letterOf = labels.ToDictionary(kv => kv.Value, kv => kv.Key[^1..]);

        var (start, end) = (report.PeriodStartUtc, report.PeriodEndUtc);
        var anomalies = await data.AnomalyCountsAsync(job.CustomerId, start, end, ct);
        var entities = (await data.MonitorsAsync(job.CustomerId, listed.Select(m => m.MonitorId).ToList(), ct)).ToDictionary(m => m.Id);
        var longest = (await data.IncidentsStartedAsync(job.CustomerId, start, end, ct))
            .Where(i => letterOf.ContainsKey(i.MonitorId))
            .GroupBy(i => i.MonitorId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => (i.ResolvedAtUtc ?? end) - i.StartedAtUtc).ThenBy(i => i.Id).First());

        foreach (var m in listed)
        {
            var x = letterOf[m.MonitorId];
            entities.TryGetValue(m.MonitorId, out var e);
            prevMonitors.TryGetValue(m.MonitorId, out var p);
            b.Section($"monitor {x} ({x}.*; name not shared)").Fact("type", m.Type);
            Put(x + ".uptime", Pct(m.UptimePercent));
            if (m.SlaTarget is not null) Put(x + ".sla_target", Pct(m.SlaTarget));
            Put(x + ".sla_status", new(NarrativeValueKind.SlaStatus, Text: m.SlaMet switch { true => "met", false => "missed", _ => "no_target" }));
            Put(x + ".downtime", Dur(m.DowntimeMinutes));
            Put(x + ".incidents", Cnt(m.IncidentCount));
            if (m.MttrMinutes is not null) Put(x + ".mttr", Dur(m.MttrMinutes));
            if (m.AvgResponseMs is not null) Put(x + ".avg_response", Ms(m.AvgResponseMs));
            if (m.P95ResponseMs is not null) Put(x + ".p95_response", Ms(m.P95ResponseMs));
            if (m.MaintenanceMinutes > 0) Put(x + ".maintenance", Dur(m.MaintenanceMinutes));
            if (anomalies.TryGetValue(m.MonitorId, out var a) && a > 0) Put(x + ".slowdowns", Cnt(a));
            if (p is not null)
            {
                Put(x + ".prev.uptime", Pct(p.UptimePercent));
                Put(x + ".prev.downtime", Dur(p.DowntimeMinutes));
                Put(x + ".prev.incidents", Cnt(p.IncidentCount));
                if (p.P95ResponseMs is not null) Put(x + ".prev.p95_response", Ms(p.P95ResponseMs));
            }
            else b.Fact("previous_period", "not monitored");
            if (e is not null && e.CreatedAtUtc >= start) b.Fact("added_this_period", "yes");
            if (e?.CertExpiresAtUtc is DateTime exp)
            {
                Put(x + ".cert_expiry", new(NarrativeValueKind.CertificateExpiry, Math.Floor((exp - end).TotalDays)));
                Put(x + ".cert_expiry_date", new(NarrativeValueKind.Date, At: MaintenanceScheduleLocal(exp, tz).Date));
            }
            if (longest.TryGetValue(m.MonitorId, out var inc))
            {
                var status = await data.FailingStatusCodeAsync(job.CustomerId, inc.MonitorId, inc.StartedAtUtc.AddMinutes(-10), inc.ResolvedAtUtc ?? end, ct);
                Put(x + ".longest_outage_start", new(NarrativeValueKind.DateTime, At: MaintenanceScheduleLocal(inc.StartedAtUtc, tz)));
                Put(x + ".longest_outage_duration", Dur(Math.Round(((inc.ResolvedAtUtc ?? end) - inc.StartedAtUtc).TotalMinutes, 1)));
                Put(x + ".longest_outage_error", new(NarrativeValueKind.ErrorClass, Text: ErrorClass(inc.Cause, status)));
                if (status is int sc) Put(x + ".longest_outage_status", new(NarrativeValueKind.StatusCode, sc));
                b.Fact("longest_outage_state", inc.ResolvedAtUtc is null ? "ongoing at the end of the period" : "resolved");
            }
        }
        if (report.Monitors.Count > listed.Count)
        {
            b.Section("Other monitors");
            Put("others.count", Cnt(report.Monitors.Count - listed.Count));
        }

        var content = b.Build();
        var language = AiJob.NormalizeLanguage(job.Language);
        return new AiPrompt(NarrativePrompts.Instructions, content, [], Output: new NarrativeOutputContract(values, labels, language));
    }

    /// <summary>"1 h 15 min" for 75 minutes (English rendering of a duration).</summary>
    public static string Human(double minutes) => NarrativeRenderer.Duration(minutes, false);

    private static DateTime MaintenanceScheduleLocal(DateTime utc, TimeZoneInfo tz) =>
        TriggerCheck.Monitoring.Maintenance.MaintenanceSchedule.UtcToLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    /// <summary>Fixed error class of an incident (never the free text): from the failing status code, else from the cause.</summary>
    public static string ErrorClass(string? cause, int? statusCode)
    {
        if (statusCode is >= 500 and <= 599) return "http_5xx";
        if (statusCode is >= 400 and <= 499) return "http_4xx";
        var c = (cause ?? "").ToLowerInvariant();
        return c switch
        {
            _ when c.Contains("timeout") || c.Contains("timed out") => "timeout",
            _ when c.Contains("dns") || c.Contains("resolve") || c.Contains("name or service") => "dns",
            _ when c.Contains("certificate") || c.Contains("ssl") || c.Contains("tls") => "tls",
            _ when c.Contains("refused") || c.Contains("reset") || c.Contains("unreachable") => "connection",
            _ when c.Contains("keyword") || c.Contains("json") => "content_check",
            _ when c.Contains("heartbeat") || c.Contains("ping") => "heartbeat_or_ping",
            _ when c.Contains("status") || c.Contains("http") => "http_status",
            _ => "other",
        };
    }
}
