using System.Globalization;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
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
/// signals and certificates through the tenant-scoped <see cref="AiTenantData"/>) and passed in as a fact; the contract
/// (<see cref="NarrativeOutputContract"/>) rejects any number of the output that is not among them. Monitors get
/// neutral labels ("monitor A"), names and targets are never sent (decision K5). No "now" in the content: an unchanged
/// period gives the same input (idempotency).
/// </summary>
public sealed class NarrativeReportProvider(AiTenantData data, SlaReportService reports) : IAiContextProvider
{
    public const int MaxMonitors = 20, MaxIncidents = 5, CauseChars = 160;

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
            .Fact("period", period.Key)
            .Fact("period_start_utc", report.PeriodStartUtc)
            .Fact("period_end_utc", report.PeriodEndUtc)
            .Identifier("time_zone", report.TimeZoneId)
            .Fact("history_incomplete", report.HistoryIncomplete ? "yes (older than the data retention; figures may be incomplete)" : "no");

        AddSummary(b, "Totals of this period", report.Summary);
        var prevMonitors = new Dictionary<int, MonitorSlaReport>();
        if (previous is { Monitors.Count: > 0 })
        {
            AddSummary(b, "Totals of the previous period (" + period.Previous().Key + ")", previous.Summary);
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

        var (start, end) = (report.PeriodStartUtc, report.PeriodEndUtc);
        var anomalies = await data.AnomalyCountsAsync(job.CustomerId, start, end, ct);
        var entities = (await data.MonitorsAsync(job.CustomerId, listed.Select(m => m.MonitorId).ToList(), ct)).ToDictionary(m => m.Id);

        b.Section("Monitors (labels only; names are not shared)");
        foreach (var m in listed)
        {
            entities.TryGetValue(m.MonitorId, out var e);
            prevMonitors.TryGetValue(m.MonitorId, out var p);
            var parts = new List<string>
            {
                $"type={m.Type}",
                "sla_target=" + Num(m.SlaTarget),
                "uptime_percent=" + Num(m.UptimePercent),
                "sla=" + (m.SlaMet switch { true => "met", false => "missed", _ => "no_target" }),
                "downtime_minutes=" + Num(m.DowntimeMinutes),
                "maintenance_minutes=" + Num(m.MaintenanceMinutes),
                "incidents=" + m.IncidentCount.ToString(CultureInfo.InvariantCulture),
                "mttr_minutes=" + Num(m.MttrMinutes),
                "avg_response_ms=" + Num(m.AvgResponseMs),
                "p95_response_ms=" + Num(m.P95ResponseMs),
                "slowdown_signals=" + (anomalies.TryGetValue(m.MonitorId, out var a) ? a : 0).ToString(CultureInfo.InvariantCulture),
            };
            if (p is not null)
                parts.AddRange(["previous_uptime_percent=" + Num(p.UptimePercent), "previous_incidents=" + p.IncidentCount.ToString(CultureInfo.InvariantCulture),
                    "previous_p95_response_ms=" + Num(p.P95ResponseMs)]);
            else parts.Add("previous_period=not_monitored");
            if (e is not null && e.CreatedAtUtc >= start) parts.Add("added_this_period=yes");
            if (e?.CertExpiresAtUtc is DateTime exp)
                parts.AddRange(["certificate_expires=" + exp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "certificate_days_left_at_period_end=" + ((int)Math.Floor((exp - end).TotalDays)).ToString(CultureInfo.InvariantCulture)]);
            b.Fact(labelOf[m.MonitorId], string.Join(' ', parts));
        }
        if (report.Monitors.Count > listed.Count)
            b.Fact("monitors_not_listed", report.Monitors.Count - listed.Count);

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
            b.Fact(key, $"monitor={labelOf[inc.MonitorId]} started_at={AiContextBuilder.Iso(inc.StartedAtUtc)} duration_minutes={Num(Math.Round(minutes, 1))}"
                + $" state={(inc.ResolvedAtUtc is null ? "ongoing_at_period_end" : "resolved")}"
                + (status is int sc ? $" last_failing_status_code={sc}" : "")
                + (inc.ResolvedDuringMaintenance ? " resolved_during_maintenance=yes" : ""));
            b.Untrusted(key + " cause", inc.Cause, CauseChars);
        }

        var content = b.Build();
        var contract = new NarrativeOutputContract(NarrativeNumbers.FactsOf(content), labels);
        return new AiPrompt(NarrativePrompts.Instructions, content, [], Output: contract);
    }

    private static void AddSummary(AiContextBuilder b, string title, SlaSummary s) =>
        b.Section(title)
            .Fact("monitors", s.MonitorCount)
            .Fact("uptime_percent", Num(s.UptimePercent))
            .Fact("downtime_minutes", Num(s.DowntimeMinutes))
            .Fact("downtime", Human(s.DowntimeMinutes))
            .Fact("incidents", s.IncidentCount)
            .Fact("mttr_minutes", Num(s.MttrMinutes))
            .Fact("avg_response_ms", Num(s.AvgResponseMs))
            .Fact("p95_response_ms", Num(s.P95ResponseMs))
            .Fact("sla_targets_met", s.SlaMet)
            .Fact("sla_targets_missed", s.SlaMissed);

    /// <summary>Precomputed changes (absolute value + direction), so the model never has to subtract.</summary>
    private static void AddChanges(AiContextBuilder b, SlaSummary now, SlaSummary prev)
    {
        b.Section("Changes compared with the previous period");
        if (now.UptimePercent is double u && prev.UptimePercent is double pu) b.Fact("uptime_change_percentage_points", Signed(Math.Round(u - pu, 4)));
        b.Fact("downtime_change_minutes", Signed(Math.Round(now.DowntimeMinutes - prev.DowntimeMinutes, 1)));
        b.Fact("incidents_change", Signed(now.IncidentCount - prev.IncidentCount));
        if (now.P95ResponseMs is int p && prev.P95ResponseMs is int pp)
        {
            b.Fact("p95_response_change_ms", Signed(p - pp));
            if (pp > 0) b.Fact("p95_response_change_percent", Signed(Math.Round(100.0 * (p - pp) / pp, 1)));
        }
        if (now.AvgResponseMs is double avg && prev.AvgResponseMs is double pavg) b.Fact("avg_response_change_ms", Signed(Math.Round(avg - pavg, 1)));
        b.Fact("monitor_count_change", Signed(now.MonitorCount - prev.MonitorCount));
    }

    internal static string Num(double? v) => v is double d ? d.ToString(CultureInfo.InvariantCulture) : "n/a";

    /// <summary>"0.05 (up)" / "3 (down)" / "0 (no change)". No "+" sign: the masker would take "+0.1234" for a phone number.</summary>
    private static string Signed(double v) =>
        Math.Abs(v).ToString(CultureInfo.InvariantCulture) + (v > 0 ? " (up)" : v < 0 ? " (down)" : " (no change)");

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
