using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Ai.Features.Postmortem;

public enum PostmortemEventKind
{
    /// <summary>First failing check of the outage (before the incident was opened: failure threshold / confirmation).</summary>
    FirstFailure,
    IncidentOpened,
    /// <summary>Another monitor of the customer (same host or same incident group) failed during the incident.</summary>
    RelatedIncident,
    MaintenanceStarted,
    MaintenanceEnded,
    Acknowledged,
    Resolved,
}

/// <summary>One timeline entry; <see cref="MonitorId"/> for <see cref="PostmortemEventKind.RelatedIncident"/>.</summary>
public sealed record PostmortemEvent(DateTime AtUtc, PostmortemEventKind Kind, int? MonitorId = null, int? IncidentId = null);

/// <summary>Another monitor that failed during the incident (names are shown in the panel only, never sent to the model).</summary>
public sealed record PostmortemRelated(int MonitorId, int IncidentId, string Name, DateTime StartedAtUtc, bool SameHost);

/// <summary>A status page of the customer that shows the incident's monitor (where the draft can be published).</summary>
public sealed record PostmortemStatusPage(int Id, string Title, string DisplayName, string TimeZoneId);

/// <summary>
/// The deterministic part of an AI-8 postmortem: timeline and impact of one resolved incident, computed from the
/// database (never by the model). Shown in the panel and offered to the model only as placeholder values.
/// </summary>
public sealed record PostmortemFacts(
    Incident Incident,
    MonitorEntity Monitor,
    DateTime ResolvedAtUtc,
    double DurationMinutes,
    DateTime? FirstFailureAtUtc,
    int FailedChecks,
    int Checks,
    int? StatusCode,
    string ErrorClass,
    IReadOnlyList<string> FailedRegions,
    IReadOnlyList<PostmortemRelated> Related,
    IReadOnlyList<MaintenanceOccurrence> Maintenance,
    IReadOnlyList<PostmortemStatusPage> StatusPages,
    IReadOnlyList<PostmortemEvent> Timeline,
    double? AcknowledgedAfterMinutes,
    string TimeZoneId,
    string? InterpretationCategory,
    AiConfidence? InterpretationConfidence,
    IReadOnlyList<CheckResult> FailingChecks)
{
    public bool MaintenanceOverlap => Maintenance.Count > 0;

    /// <summary>
    /// The values the model may reference as placeholders ("{{incident.duration}}"), rendered by the server
    /// (<see cref="PostmortemRenderer"/>). Times are local times in <see cref="TimeZoneId"/>.
    /// </summary>
    public Dictionary<string, NarrativeValue> Values()
    {
        var tz = Monitoring.Reporting.SlaReportService.ResolveZone(TimeZoneId);
        DateTime Local(DateTime utc) => MaintenanceSchedule.UtcToLocal(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var v = new Dictionary<string, NarrativeValue>(StringComparer.Ordinal)
        {
            ["incident.started_at"] = new(NarrativeValueKind.DateTime, At: Local(Incident.StartedAtUtc)),
            ["incident.resolved_at"] = new(NarrativeValueKind.DateTime, At: Local(ResolvedAtUtc)),
            ["incident.duration"] = new(NarrativeValueKind.Duration, Math.Round(DurationMinutes, 1)),
            ["incident.error"] = new(NarrativeValueKind.ErrorClass, Text: ErrorClass),
            ["incident.failed_checks"] = new(NarrativeValueKind.Count, FailedChecks),
        };
        if (FirstFailureAtUtc is DateTime first && first < Incident.StartedAtUtc)
            v["incident.first_failure_at"] = new(NarrativeValueKind.DateTime, At: Local(first));
        if (StatusCode is int sc) v["incident.status_code"] = new(NarrativeValueKind.StatusCode, sc);
        if (FailedRegions.Count > 0) v["incident.failed_regions"] = new(NarrativeValueKind.Count, FailedRegions.Count);
        if (AcknowledgedAfterMinutes is double ack) v["incident.acknowledged_after"] = new(NarrativeValueKind.Duration, Math.Round(ack, 1));
        if (Maintenance.Count > 0)
        {
            v["maintenance.started_at"] = new(NarrativeValueKind.DateTime, At: Local(Maintenance[0].StartUtc));
            v["maintenance.ended_at"] = new(NarrativeValueKind.DateTime, At: Local(Maintenance[0].EndUtc));
        }
        if (Related.Count > 0) v["related.monitors"] = new(NarrativeValueKind.Count, Related.Select(r => r.MonitorId).Distinct().Count());
        return v;
    }
}

/// <summary>
/// Loads <see cref="PostmortemFacts"/> for one incident of one tenant. Every query is filtered by the customer id (the
/// session's or the job's), like <see cref="AiTenantData"/>: another tenant's incident simply yields null.
/// </summary>
public sealed class PostmortemFactsLoader(AppDbContext db, AiTenantData data)
{
    public const int MaxRelated = 20, MaxFailingChecks = 8, MaxChecks = 5000;
    /// <summary>Failing checks this long before the incident start count as the beginning of the outage.</summary>
    public static readonly TimeSpan LeadWindow = TimeSpan.FromMinutes(30);
    /// <summary>How far around the outage a maintenance period is looked up (to show its real start and end).</summary>
    public static readonly TimeSpan MaintenanceWindow = TimeSpan.FromHours(12);

    /// <summary>Null when the incident does not exist for this customer or is still open.</summary>
    public async Task<PostmortemFacts?> LoadAsync(int customerId, int incidentId, CancellationToken ct = default)
    {
        var incident = await data.IncidentAsync(customerId, incidentId, ct);
        if (incident?.ResolvedAtUtc is not DateTime end) return null;
        var monitor = incident.Monitor;
        var start = incident.StartedAtUtc;

        var checks = await db.CheckResults.AsNoTracking()
            .Where(r => r.MonitorId == monitor.Id && r.Monitor.CustomerId == customerId && r.RegionId == null
                        && r.CheckedAtUtc >= start - LeadWindow && r.CheckedAtUtc <= end)
            .OrderBy(r => r.CheckedAtUtc).ThenBy(r => r.Id).Take(MaxChecks).ToListAsync(ct);
        // The outage began with the earliest failing check that has no successful check between it and the start.
        DateTime? firstFailure = null;
        foreach (var c in checks.Where(c => c.CheckedAtUtc <= start).Reverse())
        {
            if (c.IsUp) break;
            firstFailure = c.CheckedAtUtc;
        }
        firstFailure ??= checks.FirstOrDefault(c => !c.IsUp && c.CheckedAtUtc >= start)?.CheckedAtUtc;
        var from = firstFailure is DateTime f && f < start ? f : start;
        var during = checks.Where(c => c.CheckedAtUtc >= from).ToList();
        var failing = during.Where(c => !c.IsUp).ToList();
        var statusCode = failing.Where(c => c.StatusCode is not null).Select(c => c.StatusCode).LastOrDefault();

        var related = await RelatedAsync(customerId, incident, monitor, end, ct);
        // Maintenance periods overlapping the outage, with their real start / end (looked up in a wider window).
        var maintenance = (await data.MaintenanceAsync(customerId, monitor.Id, from - MaintenanceWindow, end + MaintenanceWindow, ct))
            .Where(m => m.StartUtc < end && m.EndUtc > from).ToList();
        var pages = await db.StatusPageMonitors.AsNoTracking()
            .Where(m => m.MonitorId == monitor.Id && m.StatusPage.CustomerId == customerId)
            .OrderBy(m => m.StatusPageId)
            .Select(m => new PostmortemStatusPage(m.StatusPageId, m.StatusPage.Title, m.DisplayName, m.StatusPage.TimeZoneId))
            .ToListAsync(ct);
        // Times in the draft are written in the zone of the first status page it would go to (else the report zone).
        var tz = pages.FirstOrDefault()?.TimeZoneId is { Length: > 0 } pageZone ? pageZone
            : await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.ReportTimeZoneId).FirstOrDefaultAsync(ct) ?? "UTC";

        var subject = incidentId.ToString(CultureInfo.InvariantCulture);
        var interpretation = AiInterpretation.TryParse(await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customerId && d.Feature == AiFeatures.IncidentInterpretation && d.SubjectType == AiSubjects.Incident
                        && d.SubjectId == subject && d.Status == AiDecisionStatus.Succeeded)
            .OrderByDescending(d => d.Id).Select(d => d.OutputJson).FirstOrDefaultAsync(ct));
        var top = interpretation?.Hypotheses.Where(h => h is not null).OrderByDescending(h => h.Confidence).FirstOrDefault();

        var timeline = new List<PostmortemEvent>();
        if (firstFailure is DateTime ff && ff < start) timeline.Add(new(ff, PostmortemEventKind.FirstFailure));
        timeline.Add(new(start, PostmortemEventKind.IncidentOpened));
        timeline.AddRange(related.Select(r => new PostmortemEvent(r.StartedAtUtc, PostmortemEventKind.RelatedIncident, r.MonitorId, r.IncidentId)));
        foreach (var m in maintenance)
        {
            timeline.Add(new(m.StartUtc, PostmortemEventKind.MaintenanceStarted));
            timeline.Add(new(m.EndUtc, PostmortemEventKind.MaintenanceEnded));
        }
        if (incident.AcknowledgedAtUtc is DateTime ack) timeline.Add(new(ack, PostmortemEventKind.Acknowledged));
        timeline.Add(new(end, PostmortemEventKind.Resolved));

        var regions = (incident.FailedRegions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        return new PostmortemFacts(
            incident, monitor, end, Math.Max(0, (end - start).TotalMinutes), firstFailure, failing.Count, during.Count, statusCode,
            NarrativeReportProvider.ErrorClass(incident.Cause, statusCode), regions, related, maintenance, pages,
            timeline.OrderBy(e => e.AtUtc).ThenBy(e => e.Kind).ToList(),
            incident.AcknowledgedAtUtc is DateTime a ? Math.Max(0, (a - start).TotalMinutes) : null,
            tz, top?.Category, top?.Confidence,
            failing.OrderByDescending(c => c.CheckedAtUtc).ThenByDescending(c => c.Id).Take(MaxFailingChecks).ToList());
    }

    /// <summary>
    /// Incidents of the customer's other monitors that overlapped this one, on the same host or in the same accepted /
    /// automatic incident group (AI-4).
    /// </summary>
    private async Task<List<PostmortemRelated>> RelatedAsync(int customerId, Incident incident, MonitorEntity monitor, DateTime end, CancellationToken ct)
    {
        var start = incident.StartedAtUtc;
        var host = AiTenantData.HostOf(monitor);
        var grouped = await db.IncidentGroupMembers.AsNoTracking()
            .Where(m => m.CustomerId == customerId && m.Included && m.IncidentId != incident.Id
                        && (m.Group.Status == IncidentGroupStatus.Accepted || m.Group.Status == IncidentGroupStatus.Auto)
                        && m.Group.Members.Any(x => x.IncidentId == incident.Id && x.Included))
            .Select(m => m.IncidentId).ToListAsync(ct);
        var rows = await db.Incidents.AsNoTracking()
            .Where(i => i.Monitor.CustomerId == customerId && i.MonitorId != monitor.Id
                        && (grouped.Contains(i.Id) || (i.StartedAtUtc <= end && (i.ResolvedAtUtc == null || i.ResolvedAtUtc >= start))))
            .OrderBy(i => i.StartedAtUtc).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.MonitorId, i.StartedAtUtc, i.Monitor.Name, i.Monitor.Type, i.Monitor.Target })
            .Take(500).ToListAsync(ct);
        return rows
            .Select(r => new { r, SameHost = host is not null && string.Equals(AiTenantData.HostOf(r.Type, r.Target), host, StringComparison.OrdinalIgnoreCase) })
            .Where(x => x.SameHost || grouped.Contains(x.r.Id))
            .Take(MaxRelated)
            .Select(x => new PostmortemRelated(x.r.MonitorId, x.r.Id, x.r.Name, x.r.StartedAtUtc, x.SameHost))
            .ToList();
    }
}
