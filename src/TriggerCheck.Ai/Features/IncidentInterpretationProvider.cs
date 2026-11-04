using System.Globalization;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Ai.Features;

/// <summary>Subject types of the AI decisions written by AI-2 / AI-3.</summary>
public static class AiSubjects
{
    public const string Incident = "Incident";
    public const string AnomalySignal = "AnomalySignal";
}

/// <summary>
/// AI-2 context: one incident of one tenant (subject = incident id). Everything is loaded through
/// <see cref="AiTenantData"/> with the job's customer id and written with <see cref="AiContextBuilder"/> (masked; error
/// texts as untrusted data; targets reduced to the host, decision K5). The content holds no "now": it only depends on
/// the stored data up to the incident's end, so re-running an unchanged incident hits the idempotency check.
/// </summary>
public sealed class IncidentInterpretationProvider(AiTenantData data, TimeProvider time) : IAiContextProvider
{
    /// <summary>Checks around the start of the incident and before its end (or now).</summary>
    public const int ChecksBeforeStart = 8, ChecksBeforeEnd = 12;
    public const int MaxErrorTexts = 5, MaxSameHostMonitors = 10;
    public const int ErrorTextChars = 300;

    public string Feature => AiFeatures.IncidentInterpretation;

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        if (!int.TryParse(job.SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var incidentId)) return null;
        var incident = await data.IncidentAsync(job.CustomerId, incidentId, ct);
        if (incident is null) return null;
        var monitor = incident.Monitor;

        var end = incident.ResolvedAtUtc ?? time.GetUtcNow().UtcDateTime;
        var checks = (await data.RecentChecksAsync(job.CustomerId, monitor.Id, ChecksBeforeEnd, end, ct))
            .Concat(await data.RecentChecksAsync(job.CustomerId, monitor.Id, ChecksBeforeStart, incident.StartedAtUtc, ct))
            .DistinctBy(c => c.Id)
            .OrderByDescending(c => c.CheckedAtUtc).ThenByDescending(c => c.Id)
            .ToList();

        var b = new AiContextBuilder(job.Language)
            .Fact("event", incident.ResolvedAtUtc is null ? "incident_opened" : "incident_resolved");
        AddMonitor(b, monitor);

        b.Section("Incident")
            .Identifier("id", incident.Id)
            .Fact("started_at", incident.StartedAtUtc)
            .Fact("resolved_at", incident.ResolvedAtUtc)
            .Fact("state", incident.ResolvedAtUtc is null ? "open" : "resolved");
        if (incident.ResolvedAtUtc is DateTime resolved)
            b.Fact("duration_seconds", (long)(resolved - incident.StartedAtUtc).TotalSeconds);
        if (incident.ResolvedDuringMaintenance) b.Fact("resolved_during_maintenance", "yes");
        b.Fact("acknowledged", incident.AcknowledgedAtUtc is null ? "no" : "yes");
        b.Untrusted("incident_cause", incident.Cause, ErrorTextChars);

        AddCertificate(b, monitor);
        AddChecks(b, checks);

        var baseline = await data.GlobalBaselineAsync(job.CustomerId, monitor.Id, ct);
        if (baseline is { SampleCount: > 0 })
            b.Section("Response time baseline (7 days, successful checks)")
                .Fact("median_ms", Math.Round(baseline.MedianMs))
                .Fact("samples", baseline.SampleCount);

        await AddSameHostAsync(b, job.CustomerId, monitor, ct);

        var anomalies = await data.AnomaliesAsync(job.CustomerId, monitor.Id, incident.StartedAtUtc.AddHours(-6), ct);
        anomalies = anomalies.Where(a => a.StartedAtUtc <= end).Take(3).ToList();
        if (anomalies.Count > 0)
        {
            b.Section("Slowdown signals (statistical baseline)");
            foreach (var a in anomalies)
                b.Fact("signal", $"metric={a.Metric} started_at={Iso(a.StartedAtUtc)} ended_at={(a.EndedAtUtc is DateTime e ? Iso(e) : "open")} peak_score={Num(a.Score)}");
        }

        var maintenance = await data.MaintenanceAsync(job.CustomerId, monitor.Id, incident.StartedAtUtc.AddHours(-2), end.AddHours(2), ct);
        b.Section("Maintenance");
        if (maintenance.Count == 0) b.Fact("periods_near_incident", "none");
        foreach (var m in maintenance) b.Fact("period", $"{Iso(m.StartUtc)} to {Iso(m.EndUtc)}");

        b.Section("Not recorded")
            .Fact("configuration_change_history", "not available (only the current configuration is known)");

        return new AiPrompt(AiFeaturePrompts.IncidentInstructions, b.Build(), AiFeaturePrompts.IncidentActions);
    }

    internal static void AddMonitor(AiContextBuilder b, MonitorEntity m)
    {
        b.Section("Monitor")
            .Identifier("name", m.Name)
            .Fact("type", m.Type)
            .Fact("target_host", AiMasker.MaskTarget(m.Type, m.Target));
        if (m.Port is int port) b.Fact("port", port);
        if (m.Type is MonitorType.Http or MonitorType.Keyword)
            b.Fact("http_method", m.HttpMethod).Fact("expected_status_codes", m.ExpectedStatusCodes);
        if (m.Type == MonitorType.Keyword)
            b.Fact("keyword_check", "the response body must contain a configured keyword (keyword not shared)");
        b.Fact("interval_seconds", m.IntervalSeconds)
            .Fact("timeout_seconds", m.TimeoutSeconds)
            .Fact("failure_threshold", m.FailureThreshold)
            .Fact("current_status", m.Status)
            .Fact("monitor_created_at", m.CreatedAtUtc);
    }

    private static void AddCertificate(AiContextBuilder b, MonitorEntity m)
    {
        if (m.CertCheckedAtUtc is null && m.CertExpiresAtUtc is null && m.CertError is null) return;
        b.Section("TLS certificate (last seen)")
            .Fact("expires_at", m.CertExpiresAtUtc)
            .Fact("checked_at", m.CertCheckedAtUtc)
            .Fact("validation_error", string.IsNullOrWhiteSpace(m.CertError) ? "none" : m.CertError);
        if (m.CertExpiresAtUtc is DateTime exp && m.CertCheckedAtUtc is DateTime at)
            b.Fact("days_left_at_check", (int)Math.Floor((exp - at).TotalDays));
    }

    private static void AddChecks(AiContextBuilder b, List<CheckResult> checks)
    {
        b.Section("Recent checks (newest first)");
        if (checks.Count == 0)
        {
            b.Fact("checks", "none recorded");
            return;
        }
        // Distinct error texts are listed once (untrusted) and referenced by key.
        var errors = new List<(string Key, string Text, List<long> Ids)>();
        foreach (var c in checks)
        {
            string? errorKey = null;
            if (!string.IsNullOrWhiteSpace(c.Error))
            {
                var e = errors.FirstOrDefault(x => x.Text == c.Error);
                if (e.Key is null && errors.Count < MaxErrorTexts)
                {
                    e = ("E" + (errors.Count + 1).ToString(CultureInfo.InvariantCulture), c.Error, []);
                    errors.Add(e);
                }
                if (e.Key is not null)
                {
                    e.Ids.Add(c.Id);
                    errorKey = e.Key;
                }
                else errorKey = "other";
            }
            b.Fact("check #" + c.Id.ToString(CultureInfo.InvariantCulture), CheckLine(c, errorKey));
        }
        if (errors.Count == 0) return;
        b.Section("Error texts of these checks");
        foreach (var e in errors)
            b.Untrusted($"error {e.Key} (checks {string.Join(", ", e.Ids.Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)))})", e.Text, ErrorTextChars);
    }

    /// <summary>"2026-03-10T11:58:00Z DOWN status=503 total_ms=1200 dns_ms=5 connect_ms=10 tls_ms=20 ttfb_ms=1100 ..."</summary>
    public static string CheckLine(CheckResult c, string? errorKey)
    {
        var parts = new List<string> { Iso(c.CheckedAtUtc), c.IsUp ? "UP" : "DOWN" };
        if (c.StatusCode is int sc) parts.Add($"status={sc}");
        parts.Add($"total_ms={c.ResponseTimeMs}");
        if (c.DnsMs is int dns) parts.Add($"dns_ms={dns}");
        if (c.ConnectMs is int connect) parts.Add($"connect_ms={connect}");
        if (c.TlsMs is int tls) parts.Add($"tls_ms={tls}");
        if (c.TtfbMs is int ttfb) parts.Add($"ttfb_ms={ttfb}");
        if (c.TtfbMs is not null && c.DnsMs is null) parts.Add("connection=reused");
        if (c.InMaintenance) parts.Add("maintenance=yes");
        if (c.IsConfirmation) parts.Add("confirmation=yes");
        if (errorKey is not null) parts.Add($"error={errorKey}");
        return string.Join(' ', parts);
    }

    private async Task AddSameHostAsync(AiContextBuilder b, int customerId, MonitorEntity monitor, CancellationToken ct)
    {
        var others = (await data.SameHostMonitorsAsync(customerId, monitor, ct)).OrderBy(m => m.Id).Take(MaxSameHostMonitors).ToList();
        b.Section("Other monitors on the same host");
        if (others.Count == 0)
        {
            b.Fact("monitors", "none");
            return;
        }
        var withIncident = await data.MonitorsWithOpenIncidentAsync(customerId, others.Select(o => o.Id).ToList(), ct);
        foreach (var o in others)
            b.Identifier("monitor", $"'{o.Name}' type={o.Type} status={o.Status} open_incident={(withIncident.Contains(o.Id) ? "yes" : "no")}"
                + (o.StatusChangedAtUtc is DateTime since ? $" status_since={Iso(since)}" : ""));
    }

    internal static string Iso(DateTime d) => AiContextBuilder.Iso(d);
    internal static string Num(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
}
