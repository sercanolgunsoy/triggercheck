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
/// AI-6 context (subject = <see cref="NarrativePeriod"/> key of one customer). Every number is computed here from the
/// database (the FEAT-10 <see cref="SlaReportService"/> for this and the previous period, plus incidents, slowdown
/// signals and certificates through the tenant-scoped <see cref="AiTenantData"/>) and written as a typed fact (unit,
/// monitor label, previous period, signed change; <see cref="GroundingFacts"/>); the contract
/// (<see cref="NarrativeOutputContract"/>) rejects any number of the output that those facts do not back. Monitors get
/// neutral labels ("monitor A"), names and targets are never sent (decision K5). No "now" in the content: an unchanged
/// period gives the same input (idempotency).
/// </summary>
public sealed class NarrativeReportProvider(AiTenantData data, SlaReportService reports) : IAiContextProvider
{
    public const int MaxMonitors = 20, MaxIncidents = 5;

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

        var b = new AiContextBuilder(job.Language);
        b.Section("Report period")
            .Fact("kind", period.Kind == ReportPeriodKind.Monthly ? "monthly" : "weekly")
            .Identifier("period", period.Key)
            .Fact("period_start_utc", report.PeriodStartUtc)
            .Fact("period_end_utc", report.PeriodEndUtc)
            .Identifier("time_zone", report.TimeZoneId)
            .Fact("history_incomplete", report.HistoryIncomplete ? "yes (older than the data retention; figures may be incomplete)" : "no");
        if (period.Kind == ReportPeriodKind.Monthly) b.Facts.AddMonth(report.Year, report.Month);
        else if (SlaReportService.TryParseWeekKey(period.Key, out _, out var weekNumber)) b.Fact("week_number", weekNumber, GroundingUnit.Count);

        AddSummary(b, "Totals of this period", "", report.Summary);
        var prevMonitors = new Dictionary<int, MonitorSlaReport>();
        if (previous is { Monitors.Count: > 0 })
        {
            AddSummary(b, "Totals of the previous period (" + period.Previous().Key + ")", "previous_", previous.Summary);
            AddChanges(b, report.Summary, previous.Summary);
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
        var labelOf = labels.ToDictionary(kv => kv.Value, kv => kv.Key);
        var slaResults = new Dictionary<string, string>();

        var (start, end) = (report.PeriodStartUtc, report.PeriodEndUtc);
        var anomalies = await data.AnomalyCountsAsync(job.CustomerId, start, end, ct);
        var entities = (await data.MonitorsAsync(job.CustomerId, listed.Select(m => m.MonitorId).ToList(), ct)).ToDictionary(m => m.Id);

        // One section per monitor: its figures are facts of that label only (a statement about monitor A may not use B's).
        foreach (var m in listed)
        {
            var label = labelOf[m.MonitorId];
            entities.TryGetValue(m.MonitorId, out var e);
            prevMonitors.TryGetValue(m.MonitorId, out var p);
            var sla = m.SlaMet switch { true => "met", false => "missed", _ => "no_target" };
            slaResults[label] = sla;
            b.Section(label + " (name not shared)").Subject(label)
                .Fact("type", m.Type)
                .Fact("sla_target", m.SlaTarget, GroundingUnit.Percent)
                .Fact("uptime_percent", m.UptimePercent)
                .Fact("sla", sla)
                .Fact("downtime_minutes", m.DowntimeMinutes)
                .Fact("incidents", m.IncidentCount, GroundingUnit.Count)
                .Fact("mttr_minutes", m.MttrMinutes)
                .Fact("avg_response_ms", m.AvgResponseMs)
                .Fact("p95_response_ms", m.P95ResponseMs);
            // Zeros only where they mean something: a "0" elsewhere must not ground "0 minutes of downtime".
            if (m.MaintenanceMinutes > 0) b.Fact("maintenance_minutes", m.MaintenanceMinutes);
            if (anomalies.TryGetValue(m.MonitorId, out var a) && a > 0) b.Fact("slowdown_signals", a, GroundingUnit.Count);
            if (p is not null)
                b.Fact("previous_uptime_percent", p.UptimePercent)
                    .Fact("previous_downtime_minutes", p.DowntimeMinutes)
                    .Fact("previous_incidents", p.IncidentCount, GroundingUnit.Count)
                    .Fact("previous_p95_response_ms", p.P95ResponseMs);
            else b.Fact("previous_period", "not monitored");
            if (e is not null && e.CreatedAtUtc >= start) b.Fact("added_this_period", "yes");
            if (e?.CertExpiresAtUtc is DateTime exp)
                b.Fact("certificate_expires", exp.Date)
                    .Fact("certificate_days_left_at_period_end", (int)Math.Floor((exp - end).TotalDays), GroundingUnit.Days);
        }
        if (report.Monitors.Count > listed.Count)
            b.Section("Other monitors").Fact("monitors_not_listed", report.Monitors.Count - listed.Count, GroundingUnit.Count);

        // Incidents: computed facts and a fixed error class only. The free-text cause is external text: it is not sent
        // (prompt injection into the official e-mail, and host names in it could de-anonymise "monitor A").
        var incidents = (await data.IncidentsStartedAsync(job.CustomerId, start, end, ct))
            .Where(i => labelOf.ContainsKey(i.MonitorId))
            .Select(i => (Incident: i, Minutes: ((i.ResolvedAtUtc ?? end) - i.StartedAtUtc).TotalMinutes))
            .OrderByDescending(x => x.Minutes).ThenBy(x => x.Incident.Id).Take(MaxIncidents).ToList();
        b.Section("Longest incidents of the period");
        if (incidents.Count == 0) b.Fact("incidents", "none");
        for (var i = 0; i < incidents.Count; i++)
        {
            var (inc, minutes) = incidents[i];
            var key = "incident " + (char)('a' + i);
            var status = await data.FailingStatusCodeAsync(job.CustomerId, inc.MonitorId, inc.StartedAtUtc.AddMinutes(-10), inc.ResolvedAtUtc ?? end, ct);
            b.Subject(labelOf[inc.MonitorId])
                .Fact(key + " monitor", labelOf[inc.MonitorId])
                .Fact(key + " started_at", inc.StartedAtUtc)
                .Fact(key + " duration_minutes", Math.Round(minutes, 1))
                .Fact(key + " state", inc.ResolvedAtUtc is null ? "ongoing at the end of the period" : "resolved")
                .Fact(key + " error_class", ErrorClass(inc.Cause, status));
            if (status is int sc) b.Fact(key + " status_code", sc);
            if (inc.ResolvedDuringMaintenance) b.Fact(key + " resolved_during_maintenance", "yes");
        }

        var content = b.Build();
        var contract = new NarrativeOutputContract(b.Facts, labels, slaResults, NarrativeOutputContract.Policy(PreviousNames(period)));
        return new AiPrompt(NarrativePrompts.Instructions, content, [], Output: contract, Facts: b.Facts);
    }

    private static void AddSummary(AiContextBuilder b, string title, string prefix, SlaSummary s) =>
        b.Section(title)
            .Fact(prefix + "monitors", s.MonitorCount, GroundingUnit.Count)
            .Fact(prefix + "uptime_percent", s.UptimePercent)
            .Fact(prefix + "downtime_minutes", s.DowntimeMinutes)
            .Identifier(prefix + "downtime", Human(s.DowntimeMinutes)) // presentation only; durations ground on the minutes
            .Fact(prefix + "incidents", s.IncidentCount, GroundingUnit.Count)
            .Fact(prefix + "mttr_minutes", s.MttrMinutes)
            .Fact(prefix + "avg_response_ms", s.AvgResponseMs)
            .Fact(prefix + "p95_response_ms", s.P95ResponseMs)
            .Fact(prefix + "sla_targets_met", s.SlaMet, GroundingUnit.Count)
            .Fact(prefix + "sla_targets_missed", s.SlaMissed, GroundingUnit.Count);

    /// <summary>Precomputed changes with their direction, so the model never subtracts and "fell by" can be checked.</summary>
    private static void AddChanges(AiContextBuilder b, SlaSummary now, SlaSummary prev)
    {
        b.Section("Changes compared with the previous period");
        if (now.UptimePercent is double u && prev.UptimePercent is double pu) b.Delta("uptime_change_percentage_points", Math.Round(u - pu, 4));
        b.Delta("downtime_change_minutes", Math.Round(now.DowntimeMinutes - prev.DowntimeMinutes, 1));
        b.Delta("incidents_change", now.IncidentCount - prev.IncidentCount, GroundingUnit.Count);
        if (now.P95ResponseMs is int p && prev.P95ResponseMs is int pp)
        {
            b.Delta("p95_response_change_ms", p - pp);
            if (pp > 0) b.Delta("p95_response_change_percent", Math.Round(100.0 * (p - pp) / pp, 1));
        }
        if (now.AvgResponseMs is double avg && prev.AvgResponseMs is double pavg) b.Delta("avg_response_change_ms", Math.Round(avg - pavg, 1));
        b.Delta("monitor_count_change", now.MonitorCount - prev.MonitorCount, GroundingUnit.Count);
    }

    /// <summary>Words that mean "the previous period" for the 0 / 100 guard: its key and month names (en, tr).</summary>
    private static string[] PreviousNames(NarrativePeriod period)
    {
        var prev = period.Previous();
        if (prev.Kind != ReportPeriodKind.Monthly) return [prev.Key];
        var m = DateTime.ParseExact(prev.Key, "yyyy-MM", CultureInfo.InvariantCulture);
        return [prev.Key, m.ToString("MMMM", CultureInfo.GetCultureInfo("en")), m.ToString("MMMM", CultureInfo.GetCultureInfo("tr"))];
    }

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

    /// <summary>"2 h 15 min" for 135.4 minutes (rounded to whole minutes; "0 min" for none).</summary>
    public static string Human(double minutes)
    {
        var total = (long)Math.Round(minutes, MidpointRounding.AwayFromZero);
        long d = total / 1440, h = total % 1440 / 60, m = total % 60;
        var parts = new List<string>();
        if (d > 0) parts.Add($"{d} d");
        if (h > 0) parts.Add($"{h} h");
        if (m > 0 || parts.Count == 0) parts.Add($"{m} min");
        return string.Join(' ', parts);
    }
}
