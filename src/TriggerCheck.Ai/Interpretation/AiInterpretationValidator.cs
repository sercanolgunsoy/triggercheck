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

    /// <summary>
    /// Contract of features with cause categories (AI-2 / AI-3), applied before <see cref="Validate"/>: a hypothesis is
    /// kept only when its category is one of <paramref name="categories"/> and at least one of its evidence_refs resolves
    /// to an item actually present in the input (<paramref name="evidenceKeys"/>). Refs are normalised ("#1234" or "1234"
    /// → "check #1234"), unresolvable ones removed. Without categories the output is returned unchanged.
    /// </summary>
    public static AiInterpretation ApplyContract(AiInterpretation raw, IReadOnlyList<string>? categories, IReadOnlySet<string>? evidenceKeys)
    {
        if (categories is not { Count: > 0 }) return raw;
        var keys = evidenceKeys ?? new HashSet<string>();
        var kept = (raw.Hypotheses ?? [])
            .Where(h => h is not null && h.Category is not null && categories.Contains(h.Category))
            .Select(h => h with { EvidenceRefs = (h.EvidenceRefs ?? []).Select(r => ResolveRef(r, keys)).OfType<string>().Distinct().Take(MaxItems).ToList() })
            .Where(h => h.EvidenceRefs!.Count > 0)
            .ToList();
        return raw with { Hypotheses = kept };
    }

    /// <summary>The input key an evidence ref points to, or null when it points to nothing that was sent.</summary>
    public static string? ResolveRef(string? reference, IReadOnlySet<string> keys)
    {
        var r = (reference ?? "").Trim();
        if (r.Length == 0) return null;
        var exact = keys.FirstOrDefault(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var digits = r.TrimStart('#');
        if (r.StartsWith("check", StringComparison.OrdinalIgnoreCase)) digits = r[5..].Trim().TrimStart('#').Trim();
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) && keys.Contains("check #" + digits) ? "check #" + digits : null;
    }

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
                Category = h.Category is null ? null : Clip(h.Category, 64),
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

/// <summary>The L3 output policy as a service (default: <see cref="AiInterpretationValidator"/>); replaceable in tests.</summary>
public interface IAiOutputValidator
{
    AiInterpretationValidator.Result? Validate(AiInterpretation raw, IReadOnlyCollection<string> allowedActions, AiAutonomy maxAutonomy);
}

public sealed class AiOutputValidator : IAiOutputValidator
{
    public AiInterpretationValidator.Result? Validate(AiInterpretation raw, IReadOnlyCollection<string> allowedActions, AiAutonomy maxAutonomy) =>
        AiInterpretationValidator.Validate(raw, allowedActions, maxAutonomy);
}
