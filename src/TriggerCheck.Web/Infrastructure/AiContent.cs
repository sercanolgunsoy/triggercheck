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

    /// <summary>Low-confidence hypotheses, shown collapsed and labelled "possible".</summary>
    public IReadOnlyList<AiHypothesis> Possible => Interpretation?.Hypotheses.Where(h => h.Confidence == AiConfidence.Low).ToList() ?? [];
}
