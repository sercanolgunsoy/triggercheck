using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Ai.Qa;

/// <summary>
/// The read-only, tenant-scoped tools of the Q&amp;A (AI-7). <b>Security contract:</b>
/// <list type="bullet">
/// <item>the customer id is a parameter of <see cref="ExecuteAsync"/>, passed by the caller from the signed-in user's
/// session; no tool has a customer / tenant argument and every query filters by that id, so an id of another tenant
/// gives the same "not_found" / empty result as an id that does not exist;</item>
/// <item>there are no write tools: queries are AsNoTracking and nothing is saved;</item>
/// <item>K5: what reaches the model is masked technical data only: ids, monitor type, host name (no URL path, query or
/// credentials), status, timings, counts and times. <b>Customer-entered monitor names are never sent</b> (as in AI-2):
/// the model refers to records with reference tokens (<see cref="QaAnswerText.MonitorRef"/>) that the page renders as
/// links with the name. A name search runs on the server and only returns which monitors matched. Keywords, JSON paths
/// and expected values are never sent. Probe error texts and incident causes come from third-party systems: they are
/// masked (<see cref="AiMasker.MaskText"/>, which also removes the keyword / JSON assertion details), truncated,
/// neutralized and sent in fields named "*_untrusted" that the system prompt declares to be data only;</item>
/// <item>every result is capped (rows, characters).</item>
/// </list>
/// </summary>
public sealed class QaToolbox(AppDbContext db, TimeProvider time, IOptions<AiOptions> options, ILogger<QaToolbox> logger)
{
    public const string ListMonitors = "list_monitors";
    public const string MonitorDetails = "monitor_details";
    public const string MonitorStats = "monitor_stats";
    public const string ListIncidents = "list_incidents";
    public const string IncidentDetails = "incident_details";

    public const int UntrustedChars = 200;
    public const int IncidentChecksBeforeEnd = 10, IncidentChecksBeforeStart = 5;
    public const int MaxNameQueryChars = 60;

    private AiQaOptions Qa => options.Value.Qa;

    private static AiToolDefinition Tool(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    private const string RangeProps = """
        "from": { "type": "string", "description": "Start of the time range (UTC), ISO 8601, e.g. 2026-03-02 or 2026-03-02T00:00:00Z." },
        "to": { "type": "string", "description": "End of the time range (UTC, exclusive), ISO 8601. Use the current time for 'until now'." }
        """;

    /// <summary>The complete tool list (read-only). Stable order and text: it is part of the cached prompt prefix.</summary>
    public static readonly IReadOnlyList<AiToolDefinition> Definitions =
    [
        Tool(ListMonitors,
            "Lists the account's monitors: id, reference, type, host, current status, interval. Monitor names are not shared " +
            "with you; to find the monitor the user means by name, pass words of the name in name_contains (matched on the " +
            "server against names and hosts). Without filters it lists all monitors (up to the row limit).",
            """
            { "type": "object", "additionalProperties": false, "properties": {
                "name_contains": { "type": "string", "description": "Part of the monitor name or host as the user wrote it (2-60 characters)." },
                "status": { "type": "string", "enum": ["up", "down", "paused", "unknown"], "description": "Only monitors with this current status." }
              } }
            """),
        Tool(MonitorDetails,
            "Configuration and current state of one monitor: type, host, port, interval, timeout, failure threshold, " +
            "expected status codes, status and since when, last check, last error, open incident, TLS certificate expiry.",
            """
            { "type": "object", "additionalProperties": false, "required": ["monitor_id"], "properties": {
                "monitor_id": { "type": "integer", "description": "Id from list_monitors." }
              } }
            """),
        Tool(MonitorStats,
            "Statistics per monitor over a time range, computed from stored checks (maintenance excluded): number of " +
            "checks, failed checks, availability (share of successful checks, %), average and maximum response time of " +
            "successful checks (ms), incidents started and downtime (minutes). Use sort_by to rank monitors " +
            "(e.g. the slowest). Omit monitor_id for all monitors.",
            "{ \"type\": \"object\", \"additionalProperties\": false, \"required\": [\"from\", \"to\"], \"properties\": {" + RangeProps + """
                ,
                "monitor_id": { "type": "integer", "description": "Only this monitor." },
                "sort_by": { "type": "string", "enum": ["slowest", "least_available", "most_incidents"], "description": "Ranking of the result rows." },
                "limit": { "type": "integer", "description": "Maximum rows (default and maximum: the row limit)." }
              } }
            """),
        Tool(ListIncidents,
            "Incidents (outages) that overlap a time range, newest first: id, reference, monitor, start, end, duration in " +
            "minutes, acknowledged, and the recorded cause text (untrusted).",
            "{ \"type\": \"object\", \"additionalProperties\": false, \"required\": [\"from\", \"to\"], \"properties\": {" + RangeProps + """
                ,
                "monitor_id": { "type": "integer", "description": "Only incidents of this monitor." },
                "open_only": { "type": "boolean", "description": "Only incidents that are still open." },
                "limit": { "type": "integer", "description": "Maximum rows (default and maximum: the row limit)." }
              } }
            """),
        Tool(IncidentDetails,
            "Details of one incident: times, duration, state, the monitor, checks around its start and end (status code, " +
            "response and phase times, error text), maintenance periods nearby and the earlier AI summary if there is one.",
            """
            { "type": "object", "additionalProperties": false, "required": ["incident_id"], "properties": {
                "incident_id": { "type": "integer", "description": "Id from list_incidents." }
              } }
            """),
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed: non-ASCII text (Turkish) stays readable and \uXXXX escapes do not add digits to the evidence. The
        // result is never embedded in HTML; untrusted text has its angle brackets neutralized already.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Runs one tool call for <paramref name="customerId"/> (from the session, never from the model). Never throws for bad
    /// input or data problems: the model gets an error result instead.
    /// </summary>
    public async Task<AiToolResult> ExecuteAsync(int customerId, AiToolCall call, CancellationToken ct)
    {
        object result;
        try
        {
            var input = call.Input.ValueKind == JsonValueKind.Object ? call.Input : default;
            result = call.Name switch
            {
                ListMonitors => await ListMonitorsAsync(customerId, input, ct),
                MonitorDetails => await MonitorDetailsAsync(customerId, input, ct),
                MonitorStats => await MonitorStatsAsync(customerId, input, ct),
                ListIncidents => await ListIncidentsAsync(customerId, input, ct),
                IncidentDetails => await IncidentDetailsAsync(customerId, input, ct),
                _ => Error("unknown_tool", "Only the listed read-only tools exist."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Q&A tool {Tool} failed", call.Name);
            result = Error("tool_failed", "The data could not be read.");
        }
        var text = JsonSerializer.Serialize(result, Json);
        if (text.Length > Math.Max(200, Qa.MaxToolResultChars))
            text = JsonSerializer.Serialize(Error("result_too_large", "Narrow the query (time range, monitor, limit)."), Json);
        return new AiToolResult(call.Id, text, IsError: result is ToolError);
    }

    public sealed record ToolError(string error, string message);

    private static ToolError Error(string code, string message) => new(code, message);

    // ---------------------------------------------------------------- tools

    private async Task<object> ListMonitorsAsync(int customerId, JsonElement input, CancellationToken ct)
    {
        var query = Str(input, "name_contains")?.Trim();
        if (query is not null && (query.Length < 2 || query.Length > MaxNameQueryChars))
            return Error("invalid_input", $"name_contains must be 2-{MaxNameQueryChars} characters.");
        var status = Str(input, "status")?.Trim().ToLowerInvariant() switch
        {
            null => (MonitorStatus?)null,
            "up" => MonitorStatus.Up,
            "down" => MonitorStatus.Down,
            "paused" => MonitorStatus.Paused,
            "unknown" => MonitorStatus.Unknown,
            _ => (MonitorStatus?)(MonitorStatus)(-1),
        };
        if (status == (MonitorStatus)(-1)) return Error("invalid_input", "status must be up, down, paused or unknown.");

        var monitors = await db.Monitors.AsNoTracking()
            .Where(m => m.CustomerId == customerId && (status == null || m.Status == status))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        // The name match runs here; the model only learns which monitors matched, never the names.
        if (query is not null) monitors = monitors.Where(m => Matches(m.Name, query) || Matches(AiTenantData.HostOf(m), query)).ToList();

        var rows = monitors.Take(Math.Max(1, Qa.MaxRows)).Select(m => new
        {
            monitor_id = m.Id,
            @ref = QaAnswerText.MonitorRef(m.Id),
            type = m.Type.ToString(),
            host = Host(m),
            status = m.Status.ToString().ToLowerInvariant(),
            enabled = m.IsEnabled,
            interval_seconds = m.IntervalSeconds,
            status_since = IsoOrNull(m.StatusChangedAtUtc),
        }).ToList();
        return Fit(rows, r => new { total = monitors.Count, returned = r.Count, truncated = r.Count < monitors.Count ? true : (bool?)null, monitors = r });
    }

    private async Task<object> MonitorDetailsAsync(int customerId, JsonElement input, CancellationToken ct)
    {
        if (Int(input, "monitor_id") is not int id) return Error("invalid_input", "monitor_id is required.");
        var m = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct);
        if (m is null) return NotFound("monitor");
        var openIncident = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == m.Id && i.Monitor.CustomerId == customerId && i.ResolvedAtUtc == null)
            .OrderByDescending(i => i.StartedAtUtc).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        var http = m.Type is MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi;
        return new
        {
            monitor_id = m.Id,
            @ref = QaAnswerText.MonitorRef(m.Id),
            type = m.Type.ToString(),
            host = Host(m),
            port = m.Port,
            http_method = http ? m.HttpMethod : null,
            expected_status_codes = http ? AiContextBuilder.FactText(m.ExpectedStatusCodes) : null,
            content_check = m.Type switch
            {
                MonitorType.Keyword => "the response must contain a configured keyword (keyword not shared)",
                MonitorType.JsonApi => "a JSON value of the response must match a configured assertion (not shared)",
                MonitorType.Heartbeat => "push monitor: DOWN when the monitored job does not call in within the interval",
                _ => null,
            },
            interval_seconds = m.IntervalSeconds,
            timeout_seconds = m.TimeoutSeconds,
            failure_threshold = m.FailureThreshold,
            enabled = m.IsEnabled,
            status = m.Status.ToString().ToLowerInvariant(),
            status_since = IsoOrNull(m.StatusChangedAtUtc),
            last_checked_at = IsoOrNull(m.LastCheckedAtUtc),
            last_response_ms = m.LastResponseTimeMs,
            last_error_untrusted = string.IsNullOrWhiteSpace(m.LastError) ? null : AiContextBuilder.UntrustedText(m.LastError, UntrustedChars),
            open_incident_id = openIncident,
            open_incident_ref = openIncident is int oi ? QaAnswerText.IncidentRef(oi) : null,
            sla_target_percent = m.SlaTarget,
            certificate_expires_at = IsoOrNull(m.CertExpiresAtUtc),
            created_at = Iso(m.CreatedAtUtc),
        };
    }

    private async Task<object> MonitorStatsAsync(int customerId, JsonElement input, CancellationToken ct)
    {
        if (Range(input) is not { } range) return RangeError();
        var (from, to) = range;
        var monitorId = Int(input, "monitor_id");
        var monitors = await db.Monitors.AsNoTracking()
            .Where(m => m.CustomerId == customerId && (monitorId == null || m.Id == monitorId))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        if (monitorId is not null && monitors.Count == 0) return NotFound("monitor");

        // Central checks only (regional results are votes of the same check); maintenance checks are not counted.
        var checks = await db.CheckResults.AsNoTracking()
            .Where(r => r.Monitor.CustomerId == customerId && (monitorId == null || r.MonitorId == monitorId) && r.RegionId == null
                        && !r.InMaintenance && r.CheckedAtUtc >= from && r.CheckedAtUtc < to)
            .GroupBy(r => r.MonitorId)
            .Select(g => new
            {
                MonitorId = g.Key,
                Checks = g.Count(),
                Up = g.Count(x => x.IsUp),
                Avg = g.Average(x => x.IsUp ? (double?)x.ResponseTimeMs : null),
                Max = g.Max(x => x.IsUp ? (int?)x.ResponseTimeMs : null),
            })
            .ToDictionaryAsync(x => x.MonitorId, ct);

        var incidents = await IncidentsOverlapping(customerId, from, to, monitorId)
            .Select(i => new { i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc })
            .Take(20_000)
            .ToListAsync(ct);
        var now = Now;

        var rows = monitors.Select(m =>
        {
            checks.TryGetValue(m.Id, out var c);
            var own = incidents.Where(i => i.MonitorId == m.Id).ToList();
            var downtime = own.Sum(i => Math.Max(0, ((i.ResolvedAtUtc ?? now) < to ? (i.ResolvedAtUtc ?? now) : to).Subtract(i.StartedAtUtc > from ? i.StartedAtUtc : from).TotalMinutes));
            return new StatsRow(m.Id, QaAnswerText.MonitorRef(m.Id), m.Type.ToString(), Host(m),
                c?.Checks ?? 0, c?.Up ?? 0, (c?.Checks ?? 0) - (c?.Up ?? 0),
                c is { Checks: > 0 } ? Math.Round(100.0 * c.Up / c.Checks, 2) : null,
                c?.Avg is double avg ? Math.Round(avg) : null,
                c?.Max,
                own.Count(i => i.StartedAtUtc >= from),
                Math.Round(downtime, 1));
        });
        rows = Str(input, "sort_by") switch
        {
            "slowest" => rows.OrderByDescending(r => r.avg_response_ms ?? -1).ThenBy(r => r.monitor_id),
            "least_available" => rows.OrderBy(r => r.availability_percent ?? 101).ThenBy(r => r.monitor_id),
            "most_incidents" => rows.OrderByDescending(r => r.incidents_started).ThenBy(r => r.monitor_id),
            _ => rows,
        };
        var list = rows.Take(Limit(input)).ToList();
        return Fit(list, r => new
        {
            from = Iso(from), to = Iso(to),
            basis = "central checks outside maintenance; availability = successful checks / checks; response times of successful checks only",
            monitors_total = monitors.Count, returned = r.Count, rows = r,
        });
    }

    public sealed record StatsRow(int monitor_id, string @ref, string type, string? host, int checks, int successful_checks, int failed_checks,
        double? availability_percent, double? avg_response_ms, int? max_response_ms, int incidents_started, double downtime_minutes);

    private async Task<object> ListIncidentsAsync(int customerId, JsonElement input, CancellationToken ct)
    {
        if (Range(input) is not { } range) return RangeError();
        var (from, to) = range;
        var monitorId = Int(input, "monitor_id");
        if (monitorId is int mid && !await db.Monitors.AsNoTracking().AnyAsync(m => m.Id == mid && m.CustomerId == customerId, ct))
            return NotFound("monitor");
        var openOnly = Bool(input, "open_only") == true;
        var q = IncidentsOverlapping(customerId, from, to, monitorId).Where(i => !openOnly || i.ResolvedAtUtc == null);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).Take(Limit(input)).ToListAsync(ct);
        var now = Now;
        var rows = items.Select(i => new
        {
            incident_id = i.Id,
            @ref = QaAnswerText.IncidentRef(i.Id),
            monitor_id = i.MonitorId,
            monitor_ref = QaAnswerText.MonitorRef(i.MonitorId),
            started_at = Iso(i.StartedAtUtc),
            resolved_at = IsoOrNull(i.ResolvedAtUtc),
            state = i.ResolvedAtUtc is null ? "open" : "resolved",
            duration_minutes = Math.Round(((i.ResolvedAtUtc ?? now) - i.StartedAtUtc).TotalMinutes, 1),
            acknowledged = i.AcknowledgedAtUtc is not null,
            resolved_during_maintenance = i.ResolvedDuringMaintenance ? true : (bool?)null,
            cause_untrusted = string.IsNullOrWhiteSpace(i.Cause) ? null : AiContextBuilder.UntrustedText(i.Cause, UntrustedChars),
        }).ToList();
        return Fit(rows, r => new { from = Iso(from), to = Iso(to), total, returned = r.Count, incidents = r });
    }

    private async Task<object> IncidentDetailsAsync(int customerId, JsonElement input, CancellationToken ct)
    {
        if (Int(input, "incident_id") is not int id) return Error("invalid_input", "incident_id is required.");
        var incident = await db.Incidents.AsNoTracking().Include(i => i.Monitor)
            .FirstOrDefaultAsync(i => i.Id == id && i.Monitor.CustomerId == customerId, ct);
        if (incident is null) return NotFound("incident");
        var m = incident.Monitor;
        var end = incident.ResolvedAtUtc ?? Now;

        IQueryable<CheckResult> Checks(DateTime before) => db.CheckResults.AsNoTracking()
            .Where(r => r.MonitorId == m.Id && r.Monitor.CustomerId == customerId && r.RegionId == null && r.CheckedAtUtc <= before)
            .OrderByDescending(r => r.CheckedAtUtc);
        var checks = (await Checks(end).Take(IncidentChecksBeforeEnd).ToListAsync(ct))
            .Concat(await Checks(incident.StartedAtUtc).Take(IncidentChecksBeforeStart).ToListAsync(ct))
            .DistinctBy(c => c.Id).OrderByDescending(c => c.CheckedAtUtc).ThenByDescending(c => c.Id).ToList();

        var maintenance = await MaintenanceQueries.OccurrencesAsync(db, customerId, m.Id, incident.StartedAtUtc.AddHours(-2), end.AddHours(2), ct);

        // AI-2 summary of this incident, if one was made (model text built from masked data; still not trusted).
        var subject = incident.Id.ToString(CultureInfo.InvariantCulture);
        var earlier = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customerId && d.Feature == AiFeatures.IncidentInterpretation && d.SubjectType == AiSubjects.Incident
                        && d.SubjectId == subject && d.Status == AiDecisionStatus.Succeeded)
            .OrderByDescending(d => d.Id).Select(d => d.OutputJson).FirstOrDefaultAsync(ct);
        var summary = AiInterpretation.TryParse(earlier)?.Summary;

        return new
        {
            incident_id = incident.Id,
            @ref = QaAnswerText.IncidentRef(incident.Id),
            started_at = Iso(incident.StartedAtUtc),
            resolved_at = IsoOrNull(incident.ResolvedAtUtc),
            state = incident.ResolvedAtUtc is null ? "open" : "resolved",
            duration_minutes = Math.Round((end - incident.StartedAtUtc).TotalMinutes, 1),
            acknowledged = incident.AcknowledgedAtUtc is not null,
            resolved_during_maintenance = incident.ResolvedDuringMaintenance ? true : (bool?)null,
            cause_untrusted = string.IsNullOrWhiteSpace(incident.Cause) ? null : AiContextBuilder.UntrustedText(incident.Cause, UntrustedChars),
            monitor = new
            {
                monitor_id = m.Id, @ref = QaAnswerText.MonitorRef(m.Id), type = m.Type.ToString(), host = Host(m),
                status = m.Status.ToString().ToLowerInvariant(), interval_seconds = m.IntervalSeconds, timeout_seconds = m.TimeoutSeconds,
                failure_threshold = m.FailureThreshold,
            },
            checks_newest_first = checks.Select(c => new
            {
                at = Iso(c.CheckedAtUtc),
                result = c.IsUp ? "up" : "down",
                status_code = c.StatusCode,
                response_ms = c.ResponseTimeMs,
                dns_ms = c.DnsMs, connect_ms = c.ConnectMs, tls_ms = c.TlsMs, ttfb_ms = c.TtfbMs,
                in_maintenance = c.InMaintenance ? true : (bool?)null,
                confirmation = c.IsConfirmation ? true : (bool?)null,
                error_untrusted = string.IsNullOrWhiteSpace(c.Error) ? null : AiContextBuilder.UntrustedText(c.Error, UntrustedChars),
            }).ToList(),
            maintenance_nearby = maintenance.Select(p => new { from = Iso(p.StartUtc), to = Iso(p.EndUtc) }).ToList(),
            earlier_ai_summary_untrusted = summary is null ? null : AiContextBuilder.UntrustedText(summary, 600),
        };
    }

    // ---------------------------------------------------------------- helpers

    private IQueryable<Incident> IncidentsOverlapping(int customerId, DateTime from, DateTime to, int? monitorId) =>
        db.Incidents.AsNoTracking()
            .Where(i => i.Monitor.CustomerId == customerId && (monitorId == null || i.MonitorId == monitorId)
                        && i.StartedAtUtc < to && (i.ResolvedAtUtc == null || i.ResolvedAtUtc >= from));

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static ToolError NotFound(string what) => Error("not_found", $"No {what} with this id in this account.");

    private ToolError RangeError() =>
        Error("invalid_range", $"from and to must be ISO 8601 UTC times with from before to, not in the future; at most {Qa.MaxRangeDays} days.");

    /// <summary>
    /// [from, to) from the input: "to" is clamped to now, "from" to at most Ai:Qa:MaxRangeDays before "to". Null when
    /// either does not parse or the range is empty.
    /// </summary>
    private (DateTime From, DateTime To)? Range(JsonElement input)
    {
        if (ParseTime(Str(input, "from")) is not DateTime from || ParseTime(Str(input, "to")) is not DateTime to) return null;
        var now = Now;
        if (to > now) to = now;
        var earliest = to.AddDays(-Math.Max(1, Qa.MaxRangeDays));
        if (from < earliest) from = earliest;
        return from < to ? (from, to) : null;
    }

    public static DateTime? ParseTime(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private int Limit(JsonElement input)
    {
        var max = Math.Max(1, Qa.MaxRows);
        return Int(input, "limit") is int l && l > 0 ? Math.Min(l, max) : max;
    }

    /// <summary>Drops rows from the end until the serialized result fits Ai:Qa:MaxToolResultChars.</summary>
    private object Fit<T>(List<T> rows, Func<List<T>, object> wrap)
    {
        var result = wrap(rows);
        while (rows.Count > 0 && JsonSerializer.Serialize(result, Json).Length > Qa.MaxToolResultChars)
        {
            rows = rows.Take(rows.Count * 2 / 3).ToList();
            result = wrap(rows);
        }
        return result;
    }

    /// <summary>Host only (K5): no URL path, query or credentials; null for push monitors.</summary>
    private static string? Host(MonitorEntity m) =>
        AiTenantData.HostOf(m) is { } host ? AiContextBuilder.FactText(host, identifier: true) : null;

    private static bool Matches(string? text, string query) =>
        text is not null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static string Iso(DateTime d) => AiContextBuilder.Iso(d);
    private static string? IsoOrNull(DateTime? d) => d is DateTime v ? Iso(v) : null;

    private static string? Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
