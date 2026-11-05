using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Model of the shared "_AiInterpretation" partial: one AI decision as the customer sees it. Pages load the decision
/// tenant-scoped (CustomerId == RequiredCustomerId) and render <c>&lt;partial name="_AiInterpretation" model="..." /&gt;</c>.
/// </summary>
/// <param name="UserFeedback">The current user's own vote (+1 / -1 / null), from AiFeedback.</param>
/// <param name="ReturnUrl">Local URL the feedback form returns to (normally the current page).</param>
public sealed record AiContentModel(long DecisionId, AiDecisionStatus Status, AiInterpretation? Interpretation, int? UserFeedback, string ReturnUrl)
{
    public static AiContentModel From(AiDecision d, string returnUrl, int? myFeedback) =>
        new(d.Id, d.Status, d.Status == AiDecisionStatus.Succeeded ? AiInterpretation.TryParse(d.OutputJson) : null, myFeedback, returnUrl);

    /// <summary>Hypotheses shown expanded (medium / high confidence).</summary>
    public IEnumerable<AiHypothesis> Likely => Interpretation?.Hypotheses.Where(h => h.Confidence > AiConfidence.Low) ?? [];

    /// <summary>English UI text (resource key) of a hypothesis category (AI-2 / AI-3); null for "unknown" / none.</summary>
    public static string? CategoryLabel(string? category) => category is not null && CategoryLabels.TryGetValue(category, out var l) ? l : null;

    private static readonly Dictionary<string, string> CategoryLabels = new()
    {
        ["dns"] = "DNS",
        ["tls_expired"] = "Certificate expired",
        ["tls_name_mismatch"] = "Certificate name mismatch",
        ["tls_untrusted"] = "Untrusted certificate",
        ["http_5xx"] = "Server error (5xx)",
        ["http_4xx"] = "Client error (4xx)",
        ["timeout"] = "Timeout",
        ["connection_refused"] = "Connection refused",
        ["network_unreachable"] = "Network unreachable",
        ["keyword_missing"] = "Expected content missing",
        ["host_outage"] = "Host-wide outage",
        ["slow_response"] = "Slow response",
        ["maintenance_related"] = "Maintenance",
        ["config_change"] = "Configuration change",
        ["heartbeat_missed"] = "Missed heartbeat",
        ["slow_dns"] = "Slow DNS",
        ["slow_connect"] = "Slow connection",
        ["slow_tls"] = "Slow TLS handshake",
        ["slow_ttfb"] = "Slow server response",
    };

    /// <summary>Low-confidence hypotheses, shown collapsed and labelled "possible".</summary>
    public IReadOnlyList<AiHypothesis> Possible => Interpretation?.Hypotheses.Where(h => h.Confidence == AiConfidence.Low).ToList() ?? [];
}
