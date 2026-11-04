using System.Text.Json;
using System.Text.Json.Serialization;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Interpretation;

/// <summary>
/// The structured-output contract of every interpreting AI feature (docs/BACKLOG.md, "Güven skoru ve açıklanabilirlik").
/// JSON names are snake_case; confidence is "low" / "medium" / "high"; autonomy is "A0".."A3".
/// </summary>
public sealed record AiInterpretation(
    string Summary,
    IReadOnlyList<AiHypothesis> Hypotheses,
    IReadOnlyList<AiSuggestedAction> SuggestedActions,
    IReadOnlyList<string> Unknowns)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Names only: "0" / 2 are not valid confidence or autonomy values.
        Converters =
        {
            new JsonStringEnumConverter<AiConfidence>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false),
            new JsonStringEnumConverter<AiAutonomy>(namingPolicy: null, allowIntegerValues: false),
        },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses stored or model JSON; null when it is not a valid interpretation.</summary>
    public static AiInterpretation? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = JsonSerializer.Deserialize<AiInterpretation>(json, Json);
            return r is { Summary: not null, Hypotheses: not null, SuggestedActions: not null, Unknowns: not null } ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <param name="Category">Cause category from the feature's fixed list (AI-2 / AI-3); null for features without one.</param>
/// <param name="EvidenceRefs">Keys of the input items the hypothesis relies on ("check #1234", "ttfb"); checked against the input.</param>
public sealed record AiHypothesis(string Cause, AiConfidence Confidence, IReadOnlyList<string> Evidence,
    string? Category = null, IReadOnlyList<string>? EvidenceRefs = null);

/// <param name="Action">Code from the feature's fixed allow-list; anything else is dropped.</param>
/// <param name="Description">Human readable explanation of the action.</param>
public sealed record AiSuggestedAction(string Action, string Description, AiAutonomy Autonomy, bool Reversible);

/// <summary>JSON schema for structured outputs (only the subset the API supports: every object closed, all fields required).</summary>
public static class AiInterpretationSchema
{
    /// <summary>
    /// Builds the schema. <paramref name="allowedActions"/> becomes an enum on suggested_actions[].action, so the model
    /// can only choose from the fixed list (an empty list leaves it a string; validation then drops every action).
    /// </summary>
    /// <param name="categories">
    /// When given, each hypothesis also requires "category" (this enum) and "evidence_refs" (input keys), the contract of
    /// AI-2 / AI-3.
    /// </param>
    public static IReadOnlyDictionary<string, JsonElement> Build(IReadOnlyList<string> allowedActions, IReadOnlyList<string>? categories = null)
    {
        var hypothesisProps = new Dictionary<string, object>
        {
            ["cause"] = new { type = "string" },
            ["confidence"] = new { type = "string", @enum = new[] { "low", "medium", "high" } },
            ["evidence"] = new { type = "array", items = new { type = "string" }, description = "Concrete facts from the input (check, metric, value)." },
        };
        var hypothesisRequired = new List<string> { "cause", "confidence", "evidence" };
        if (categories is { Count: > 0 })
        {
            hypothesisProps["category"] = new { type = "string", @enum = categories };
            hypothesisProps["evidence_refs"] = new
            {
                type = "array", items = new { type = "string" },
                description = "Keys of the input lines this hypothesis relies on, exactly as written before the colon, e.g. \"check #1234\", \"ttfb\", \"validation_error\", \"error E1\". At least one."
            };
            hypothesisRequired.AddRange(["category", "evidence_refs"]);
        }
        object actionProp = allowedActions.Count > 0
            ? new { type = "string", @enum = allowedActions }
            : new { type = "string" };
        var root = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "summary", "hypotheses", "suggested_actions", "unknowns" },
            ["properties"] = new Dictionary<string, object>
            {
                ["summary"] = new { type = "string", description = "1-3 sentences in the requested language." },
                ["hypotheses"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = hypothesisRequired,
                        properties = hypothesisProps,
                    },
                },
                ["suggested_actions"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "action", "description", "autonomy", "reversible" },
                        properties = new Dictionary<string, object>
                        {
                            ["action"] = actionProp,
                            ["description"] = new { type = "string" },
                            ["autonomy"] = new { type = "string", @enum = new[] { "A0", "A1", "A2", "A3" } },
                            ["reversible"] = new { type = "boolean" },
                        },
                    },
                },
                ["unknowns"] = new { type = "array", items = new { type = "string" } },
            },
        };
        var element = JsonSerializer.SerializeToElement(root);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
