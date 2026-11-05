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
    /// to an item actually present in the input (<paramref name="evidenceKeys"/>) that may support that category
    /// (<paramref name="refAllowed"/>). Refs are normalised ("#1234", "1234", "check #0012", "check #12: status 503",
    /// "E1"); unresolvable or not allowed ones are removed. Without categories the output is returned unchanged.
    /// </summary>
    public static AiInterpretation ApplyContract(AiInterpretation raw, IReadOnlyList<string>? categories, IReadOnlySet<string>? evidenceKeys,
        Func<string, string, bool>? refAllowed = null)
    {
        if (categories is not { Count: > 0 }) return raw;
        var keys = evidenceKeys ?? new HashSet<string>();
        var kept = (raw.Hypotheses ?? [])
            .Where(h => h is not null && h.Category is not null && categories.Contains(h.Category))
            .Select(h => h with { EvidenceRefs = GroundedRefs(h, keys, refAllowed).Take(MaxItems).ToList() })
            .Where(h => h.EvidenceRefs!.Count > 0)
            .ToList();
        return raw with { Hypotheses = kept };
    }

    /// <summary>The hypothesis' refs that resolve to a sent key allowed for its category (normalised, distinct).</summary>
    public static IEnumerable<string> GroundedRefs(AiHypothesis h, IReadOnlySet<string> keys, Func<string, string, bool>? refAllowed) =>
        (h.EvidenceRefs ?? []).Select(r => ResolveRef(r, keys)).OfType<string>()
            .Where(k => refAllowed is null || (h.Category is not null && refAllowed(h.Category, k)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The input key an evidence ref points to, or null when it points to nothing that was sent.</summary>
    public static string? ResolveRef(string? reference, IReadOnlySet<string> keys)
    {
        var r = (reference ?? "").Trim();
        var colon = r.IndexOf(':');
        if (colon >= 0) r = r[..colon].Trim(); // "check #12: status 503" → "check #12"
        if (r.Length == 0) return null;
        var exact = keys.FirstOrDefault(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        string? Key(string candidate) => keys.FirstOrDefault(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase));
        // Bare error block id: "E1" → "error E1".
        if (r.Length > 1 && (r[0] is 'E' or 'e') && int.TryParse(r.AsSpan(1), System.Globalization.NumberStyles.None, null, out var e))
            return Key("error E" + e.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Check id: "#12", "12", "check 12", "check #0012" → "check #12".
        var digits = r.StartsWith("check", StringComparison.OrdinalIgnoreCase) ? r[5..] : r;
        digits = digits.Trim().TrimStart('#').Trim();
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? Key("check #" + id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
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

/// <summary>
/// Default output contract of the interpreting features (AI-2, AI-3, AI-5): the shared schema, then the category /
/// evidence_refs contract (<see cref="AiInterpretationValidator.ApplyContract"/>), then number grounding when the prompt
/// carries facts (<see cref="Grounding.NumberGrounding.Apply"/>, strict: numbers as given), then the L3 policy.
/// </summary>
public sealed class AiInterpretationContract(Execution.AiPrompt prompt, IAiOutputValidator validator, AiAutonomy maxAutonomy) : Execution.IAiOutputContract
{
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Schema { get; } = AiInterpretationSchema.Build(prompt.AllowedActions, prompt.Categories);

    public Execution.AiOutputCheck Check(string? json)
    {
        var parsed = AiInterpretation.TryParse(json);
        if (parsed is not null) parsed = AiInterpretationValidator.ApplyContract(parsed, prompt.Categories, prompt.EvidenceKeys, prompt.RefAllowed);
        if (parsed is not null && prompt.Facts is not null) parsed = Grounding.NumberGrounding.Apply(parsed, prompt.Facts, Grounding.GroundingPolicy.Strict);
        var valid = parsed is null ? null : validator.Validate(parsed, prompt.AllowedActions, maxAutonomy);
        return valid is null
            ? new(null, "invalid_output")
            : new(valid.Interpretation.ToJson(), null, valid.MaxConfidence, valid.AutonomyLevel);
    }
}
