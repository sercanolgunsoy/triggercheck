using System.Globalization;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Correlation;

namespace TriggerCheck.Ai.Features.Correlation;

/// <summary>
/// AI-4 context: one incident group of one tenant (subject = group id), loaded through the tenant-filtered
/// <see cref="IncidentCorrelationService.GetAsync"/>. The model sees neutral labels ("incident A" = primary), never
/// monitor names; targets are reduced to the host (K5) and error texts are masked untrusted data. The content holds no
/// "now" (offsets are relative to the primary), so an unchanged group hits the interpreter's idempotency check.
/// </summary>
public sealed class CorrelationGroupingProvider(IncidentCorrelationService groups) : IAiContextProvider
{
    public const int MaxRuleMembers = 15, ErrorTextChars = 300;

    public string Feature => AiFeatures.CorrelationGrouping;

    /// <summary>The members as they are labelled in the context: primary first, rule members, then candidates (by start).</summary>
    public static IReadOnlyList<IncidentGroupMember> Ordered(IncidentGroup g)
    {
        static IEnumerable<IncidentGroupMember> By(IncidentGroup g, IncidentGroupRole role) =>
            g.Members.Where(m => m.Role == role).OrderBy(m => m.Incident.StartedAtUtc).ThenBy(m => m.IncidentId);
        return By(g, IncidentGroupRole.Primary).Take(1)
            .Concat(By(g, IncidentGroupRole.Rule).Take(MaxRuleMembers))
            .Concat(By(g, IncidentGroupRole.Ambiguous).Take(CorrelationRules.MaxAmbiguous))
            .ToList();
    }

    /// <summary>Context key → incident id of the candidates (ambiguous members) as labelled for the model.</summary>
    public static IReadOnlyDictionary<string, int> CandidateKeys(IncidentGroup g)
    {
        var ordered = Ordered(g);
        return ordered.Select((m, i) => (m, i)).Where(x => x.m.Role == IncidentGroupRole.Ambiguous)
            .ToDictionary(x => "incident " + CorrelationPrompts.Label(x.i), x => x.m.IncidentId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        if (!int.TryParse(job.SubjectId, NumberStyles.None, CultureInfo.InvariantCulture, out var groupId)) return null;
        var g = await groups.GetAsync(job.CustomerId, groupId, ct);
        if (g is null) return null;
        var ordered = Ordered(g);
        if (ordered.Count < 2 || ordered[0].Role != IncidentGroupRole.Primary) return null;

        var primary = ordered[0].Incident;
        var primaryDescribed = Describe(primary);
        var b = new AiContextBuilder(job.Language);
        b.Section("Incident group")
            .Fact("rule_signals", string.IsNullOrEmpty(g.Signals) ? "none" : g.Signals.Replace(",", ", "))
            .Fact("start_spread_seconds", (long)(ordered.Max(m => m.Incident.StartedAtUtc) - primary.StartedAtUtc).TotalSeconds);
        var hiddenRules = g.Members.Count(m => m.Role == IncidentGroupRole.Rule) - ordered.Count(m => m.Role == IncidentGroupRole.Rule);
        if (hiddenRules > 0) b.Fact("more_rule_grouped_incidents_not_listed", hiddenRules);

        string? section = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            var title = m.Role switch
            {
                IncidentGroupRole.Primary => "Primary incident",
                IncidentGroupRole.Rule => "Grouped by the rules",
                _ => "Candidates (decide for each)",
            };
            if (title != section) b.Section(section = title);
            var label = CorrelationPrompts.Label(i);
            var line = Line(m, primaryDescribed);
            b.Fact("incident " + label, line);
            // K5: masked explicitly (and again by Untrusted): keyword / JSON assertion errors lose the configured keyword,
            // path and values; credentials, e-mail addresses and URL paths are removed.
            b.Untrusted("error " + label, AiMasker.MaskText(m.Incident.Cause), ErrorTextChars);
        }
        return new AiPrompt(CorrelationPrompts.Instructions, b.Build(), [], CorrelationPrompts.Categories,
            AiFeaturePrompts.EvidenceKeys(b), CorrelationPrompts.IsAllowed);
    }

    private static CorrelationIncident Describe(Incident i) =>
        CorrelationRules.Describe(i.Id, i.MonitorId, i.Monitor.Type, i.Monitor.Target, i.Cause, i.StartedAtUtc);

    /// <summary>"type=Http host=api.example.test error_type=timeout started_offset_seconds=40 state=open link=same_host,same_window"</summary>
    private static string Line(IncidentGroupMember m, CorrelationIncident primary)
    {
        var i = m.Incident;
        var d = Describe(i);
        var parts = new List<string> { $"type={i.Monitor.Type}" };
        if (i.Monitor.Type != MonitorType.Heartbeat) parts.Add("host=" + (d.Host ?? "unknown"));
        if (i.Monitor.Port is int port) parts.Add($"port={port}");
        parts.Add("error_type=" + d.ErrorType);
        parts.Add("started_offset_seconds=" + ((long)(i.StartedAtUtc - primary.StartedAtUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture));
        parts.Add("state=" + (i.ResolvedAtUtc is null ? "open" : "resolved"));
        if (i.ResolvedAtUtc is DateTime r) parts.Add("duration_seconds=" + ((long)(r - i.StartedAtUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture));
        if (m.Role != IncidentGroupRole.Primary)
        {
            var link = CorrelationRules.Link(primary, d, TimeSpan.FromHours(1));
            parts.Add("link_to_primary=" + (link.Signals.Count == 0 ? "none" : string.Join(",", link.Signals)));
        }
        return string.Join(' ', parts);
    }
}
