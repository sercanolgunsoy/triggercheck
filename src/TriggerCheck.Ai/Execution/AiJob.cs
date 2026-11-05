namespace TriggerCheck.Ai.Execution;

/// <summary>
/// A request to run one AI feature for one subject of one tenant, e.g. (IncidentInterpretation, customer 3, "Incident", "42").
/// <see cref="CustomerId"/> must come from the session or from the subject's owner, never from user or model input.
/// </summary>
public sealed record AiJob(string Feature, int CustomerId, string SubjectType, string SubjectId, string Language = "en")
{
    /// <summary>The supported output languages: "tr" for Turkish, "en" for everything else.</summary>
    public static string NormalizeLanguage(string? language) =>
        language is not null && language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
}

/// <summary>Model input for one job, produced by the feature's <see cref="IAiContextProvider"/>.</summary>
/// <param name="FeatureInstructions">Static, feature-specific instructions appended to the cached system prompt.</param>
/// <param name="UserContent">Per-call data built with <see cref="Context.AiContextBuilder"/> (masked, untrusted text delimited).</param>
/// <param name="AllowedActions">Fixed allow-list of action codes the feature may suggest (may be empty).</param>
/// <param name="Categories">
/// Optional cause categories: when set, every hypothesis must carry one of them ("category", an enum in the schema) and
/// cite "evidence_refs" that resolve to <paramref name="EvidenceKeys"/>; hypotheses that do not are dropped.
/// </param>
/// <param name="EvidenceKeys">Keys of the evidence items present in <paramref name="UserContent"/> ("check #1234", "ttfb", ...).</param>
/// <param name="RefAllowed">(category, key) → whether that key may support a hypothesis of that category.</param>
/// <param name="Grounding">
/// Optional feature-specific output check, applied after the category contract: returns the output with ungrounded
/// parts removed, or null when it is unusable (recorded as Failed / "invalid_output"). AI-5 uses it to drop text with
/// numbers that are not in the input (<see cref="Interpretation.AiNumberGrounding"/>).
/// </param>
/// <param name="Output">
/// Optional own output contract (schema + validation) for features whose answer is not an AiInterpretation (AI-6
/// narrative). Null: the shared interpretation schema and L3 validation.
/// </param>
public sealed record AiPrompt(string FeatureInstructions, string UserContent, IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string>? Categories = null, IReadOnlySet<string>? EvidenceKeys = null, Func<string, string, bool>? RefAllowed = null,
    Func<Interpretation.AiInterpretation, Interpretation.AiInterpretation?>? Grounding = null,
    IAiOutputContract? Output = null);

/// <summary>A feature-specific structured output: the JSON schema sent to the model and the validation of its answer.</summary>
public interface IAiOutputContract
{
    IReadOnlyDictionary<string, System.Text.Json.JsonElement> Schema { get; }

    /// <summary>Validated JSON to store, or null JSON with an error code (the decision becomes Failed).</summary>
    AiOutputCheck Check(string? json);
}

/// <param name="Json">Validated output to store (null: rejected).</param>
/// <param name="ErrorCode">Short machine code when rejected, e.g. "invalid_output".</param>
public sealed record AiOutputCheck(string? Json, string? ErrorCode = null);

/// <summary>
/// Builds the model input for one feature (registered per feature in DI). Must load data only through
/// <see cref="Context.AiTenantData"/> (tenant-scoped) and must build the content with <see cref="Context.AiContextBuilder"/>.
/// </summary>
public interface IAiContextProvider
{
    string Feature { get; }

    /// <summary>Null when the subject no longer exists or there is nothing to interpret (nothing is recorded).</summary>
    Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct);
}
