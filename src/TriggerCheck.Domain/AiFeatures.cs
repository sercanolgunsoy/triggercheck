using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Domain;

/// <summary>
/// Catalog of the AI features (docs/BACKLOG.md, AI-2..AI-8) and the plan rules for them (AI-10). A plan stores the
/// names of its features in <see cref="Plan.AiFeatures"/>.
/// </summary>
public static class AiFeatures
{
    public const string IncidentInterpretation = "IncidentInterpretation";   // AI-2
    public const string SlowdownExplanation = "SlowdownExplanation";         // AI-3
    public const string CorrelationGrouping = "CorrelationGrouping";         // AI-4
    public const string SettingsSuggestions = "SettingsSuggestions";         // AI-5
    public const string NarrativeReport = "NarrativeReport";                 // AI-6
    public const string QuestionAnswer = "QuestionAnswer";                   // AI-7
    public const string PostmortemDraft = "PostmortemDraft";                 // AI-8

    /// <param name="MaxAutonomy">Highest autonomy level the feature may produce today; model output is clamped to it.</param>
    public sealed record Info(string Name, string BacklogId, AiAutonomy MaxAutonomy);

    /// <summary>All features, in plan-tier order. AI-4 starts at A1 (decision K3) and AI-8 drafts need approval (A2).</summary>
    public static readonly IReadOnlyList<Info> All =
    [
        new(IncidentInterpretation, "AI-2", AiAutonomy.A0),
        new(SlowdownExplanation, "AI-3", AiAutonomy.A0),
        new(SettingsSuggestions, "AI-5", AiAutonomy.A1),
        new(CorrelationGrouping, "AI-4", AiAutonomy.A1),
        new(NarrativeReport, "AI-6", AiAutonomy.A0),
        new(QuestionAnswer, "AI-7", AiAutonomy.A0),
        new(PostmortemDraft, "AI-8", AiAutonomy.A2),
    ];

    public static Info? Find(string? name) => All.FirstOrDefault(f => f.Name == name);

    /// <summary>Parses a plan's comma separated list; unknown names are dropped.</summary>
    public static IReadOnlyList<string> Parse(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => Find(n) is not null).Distinct().ToList();

    /// <summary>Normalized comma separated list (catalog order, known names only).</summary>
    public static string Format(IEnumerable<string>? names)
    {
        var set = (names ?? []).ToHashSet();
        return string.Join(",", All.Where(f => set.Contains(f.Name)).Select(f => f.Name));
    }

    public static bool PlanAllows(Plan plan, string feature) => plan.AiMonthlyCallLimit > 0 && Parse(plan.AiFeatures).Contains(feature);
}
