using System.Globalization;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Features.Postmortem;

/// <summary>
/// AI-8 context (subject = a resolved incident of one tenant). The deterministic facts (<see cref="PostmortemFacts"/>:
/// timeline, duration, failed checks, error class, status code, regions, related monitors) are computed from the
/// database and offered to the model only as placeholder keys rendered by the server, like AI-6: the model writes no
/// numbers. K5: the target is reduced to its host, monitor names (internal and public) are never sent, error texts are
/// masked (<see cref="AiMasker"/>) and wrapped as untrusted data. No "now" in the content: an unchanged incident gives
/// the same input (idempotent).
/// </summary>
public sealed class PostmortemDraftProvider(PostmortemFactsLoader loader) : IAiContextProvider
{
    public const string SubjectType = AiSubjects.Incident;
    public const int MaxErrorTexts = 3, ErrorTextChars = 300;

    public string Feature => AiFeatures.PostmortemDraft;

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        if (job.SubjectType != SubjectType || !int.TryParse(job.SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var incidentId)) return null;
        var facts = await loader.LoadAsync(job.CustomerId, incidentId, ct);
        if (facts is null) return null; // gone, another tenant's or still open

        var values = facts.Values();
        var b = new AiContextBuilder(job.Language);
        b.Section("Incident facts (computed by TriggerCheck; insert them only as {{key}})");
        b.Identifier(PostmortemRenderer.ServiceKey, "the public name of the affected service (not shared)");
        foreach (var (key, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            b.Identifier(key, PostmortemRenderer.Format(value, "en", facts.TimeZoneId)); // shown as it will be rendered

        b.Section("Context");
        b.Fact("monitor_type", facts.Monitor.Type);
        if (facts.Monitor.Type != MonitorType.Heartbeat) b.Fact("target_host", AiMasker.MaskTarget(facts.Monitor.Type, facts.Monitor.Target));
        b.Fact("maintenance_overlapped", facts.MaintenanceOverlap ? "yes" : "no");
        if (facts.Incident.ResolvedDuringMaintenance) b.Fact("resolved_during_maintenance", "yes");
        b.Fact("acknowledged_by_team", facts.Incident.AcknowledgedAtUtc is null ? "no" : "yes");
        b.Fact("multi_region_decision", facts.FailedRegions.Count > 0 ? "yes" : "no");
        b.Fact("other_monitors_failed_same_host", facts.Related.Any(r => r.SameHost) ? "yes" : "no");
        b.Fact("other_monitors_failed_same_group", facts.Related.Any(r => !r.SameHost) ? "yes" : "no");
        if (facts.Monitor.CertExpiresAtUtc is DateTime certExpiry)
            b.Fact("certificate_expired_during_incident", certExpiry <= facts.ResolvedAtUtc ? "yes" : "no");
        if (!string.IsNullOrWhiteSpace(facts.Monitor.CertError)) b.Untrusted("certificate_validation_error", facts.Monitor.CertError, ErrorTextChars);
        if (facts.InterpretationCategory is { } category)
            b.Fact("earlier_incident_analysis", $"category={category} confidence={facts.InterpretationConfidence?.ToString().ToLowerInvariant() ?? "low"}");

        b.Section("Failing checks (evidence for the likely cause; never quote their numbers)");
        if (facts.FailingChecks.Count == 0) b.Fact("checks", "none recorded");
        var errors = new List<(string Key, string Norm, string Text)>();
        foreach (var c in facts.FailingChecks)
        {
            string? errorKey = null;
            if (!string.IsNullOrWhiteSpace(c.Error))
            {
                var norm = IncidentInterpretationProvider.NormalizeError(c.Error);
                var e = errors.FirstOrDefault(x => x.Norm == norm);
                if (e.Key is null && errors.Count < MaxErrorTexts)
                {
                    e = ("E" + (errors.Count + 1).ToString(CultureInfo.InvariantCulture), norm, c.Error);
                    errors.Add(e);
                }
                errorKey = e.Key ?? "other";
            }
            b.Fact("check", IncidentInterpretationProvider.CheckLine(c, errorKey));
        }
        if (errors.Count > 0 || !string.IsNullOrWhiteSpace(facts.Incident.Cause))
        {
            b.Section("Error texts (from the monitored system)");
            b.Untrusted("incident_cause", facts.Incident.Cause, ErrorTextChars);
            foreach (var e in errors) b.Untrusted("error " + e.Key, e.Text, ErrorTextChars);
        }

        var language = AiJob.NormalizeLanguage(job.Language);
        return new AiPrompt(PostmortemPrompts.Instructions, b.Build(), [], Output: new PostmortemOutputContract(values, language, facts.TimeZoneId));
    }
}
