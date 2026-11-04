using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Interpretation;

/// <summary>
/// L3 policy applied to every model output before it is stored or shown:
/// <list type="bullet">
/// <item>hypotheses without evidence are dropped (never shown);</item>
/// <item>suggested actions not in the feature's allow-list are dropped (the model can never invent an action);</item>
/// <item>autonomy is clamped to the feature's maximum level;</item>
/// <item>lengths and counts are capped (the UI renders the text as-is, HTML-encoded).</item>
/// </list>
/// Nothing here executes an action: actions are only ever suggestions to a human or to a later L3 rule.
/// </summary>
public static class AiInterpretationValidator
{
    public const int MaxSummary = 1000, MaxText = 500, MaxItems = 8;

    public sealed record Result(AiInterpretation Interpretation, AiConfidence? MaxConfidence, AiAutonomy AutonomyLevel);

    /// <returns>Null when the output is unusable (no summary).</returns>
    public static Result? Validate(AiInterpretation raw, IReadOnlyCollection<string> allowedActions, AiAutonomy maxAutonomy)
    {
        var summary = Clip(raw.Summary, MaxSummary);
        if (summary.Length == 0) return null;

        var hypotheses = (raw.Hypotheses ?? [])
            .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Cause))
            .Select(h => h with
            {
                Cause = Clip(h.Cause, MaxText),
                Evidence = (h.Evidence ?? []).Select(e => Clip(e, MaxText)).Where(e => e.Length > 0).Take(MaxItems).ToList()
            })
            .Where(h => h.Evidence.Count > 0)
            .OrderByDescending(h => h.Confidence)
            .Take(MaxItems)
            .ToList();

        var actions = (raw.SuggestedActions ?? [])
            .Where(a => a is not null && allowedActions.Contains(a.Action))
            .Select(a => a with
            {
                Description = Clip(a.Description, MaxText),
                Autonomy = a.Autonomy > maxAutonomy ? maxAutonomy : a.Autonomy
            })
            .Take(MaxItems)
            .ToList();

        var unknowns = (raw.Unknowns ?? []).Select(u => Clip(u, MaxText)).Where(u => u.Length > 0).Take(MaxItems).ToList();

        var clean = new AiInterpretation(summary, hypotheses, actions, unknowns);
        AiConfidence? maxConfidence = hypotheses.Count > 0 ? hypotheses.Max(h => h.Confidence) : null;
        var level = actions.Count > 0 ? actions.Max(a => a.Autonomy) : AiAutonomy.A0;
        return new Result(clean, maxConfidence, level);
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (char.IsHighSurrogate(s[cut - 1])) cut--; // never split a surrogate pair (emoji etc.)
        return s[..cut] + "…";
    }
}
