namespace TriggerCheck.Ai.Execution;

/// <summary>
/// A request to run one AI feature for one subject of one tenant, e.g. (IncidentInterpretation, customer 3, "Incident", "42").
/// <see cref="CustomerId"/> must come from the session or from the subject's owner, never from user or model input.
/// </summary>
public sealed record AiJob(string Feature, int CustomerId, string SubjectType, string SubjectId, string Language = "en");

/// <summary>Model input for one job, produced by the feature's <see cref="IAiContextProvider"/>.</summary>
/// <param name="FeatureInstructions">Static, feature-specific instructions appended to the cached system prompt.</param>
/// <param name="UserContent">Per-call data built with <see cref="Context.AiContextBuilder"/> (masked, untrusted text delimited).</param>
/// <param name="AllowedActions">Fixed allow-list of action codes the feature may suggest (may be empty).</param>
public sealed record AiPrompt(string FeatureInstructions, string UserContent, IReadOnlyList<string> AllowedActions);

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
