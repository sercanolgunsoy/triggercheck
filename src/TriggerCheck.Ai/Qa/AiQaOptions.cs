namespace TriggerCheck.Ai.Qa;

/// <summary>
/// "Ai:Qa": limits of the natural-language Q&amp;A (AI-7). Every limit bounds cost or the size of what reaches the
/// model; see docs/DEPLOYMENT.md (AI section). Model / effort / output cap per call come from
/// Ai:Features:QuestionAnswer:* (effort defaults to <see cref="Effort"/> when that is not set).
/// </summary>
public sealed class AiQaOptions
{
    /// <summary>Longest question accepted (characters).</summary>
    public int MaxQuestionChars { get; set; } = 500;
    /// <summary>Model calls per question (the tool loop); the last one must answer without tools.</summary>
    public int MaxIterations { get; set; } = 6;
    /// <summary>Tool calls per question in total; further calls get an error result and the model must answer.</summary>
    public int MaxToolCalls { get; set; } = 12;
    /// <summary>Characters of one tool result; longer lists are cut (and marked truncated).</summary>
    public int MaxToolResultChars { get; set; } = 6000;
    /// <summary>Characters of all tool results of one question.</summary>
    public int MaxTotalToolResultChars { get; set; } = 40_000;
    /// <summary>Rows per list tool result.</summary>
    public int MaxRows { get; set; } = 50;
    /// <summary>Longest time range a statistics / incident query may cover (days).</summary>
    public int MaxRangeDays { get; set; } = 90;
    /// <summary>Earlier questions and answers of the page session sent along for follow-up questions.</summary>
    public int MaxHistoryTurns { get; set; } = 3;
    /// <summary>Characters of each earlier answer sent along.</summary>
    public int MaxHistoryAnswerChars { get; set; } = 1500;
    /// <summary>Output token cap of one model call (thinking + answer) unless Ai:Features:QuestionAnswer:MaxTokens is set.</summary>
    public int MaxTokensPerCall { get; set; } = 8000;
    /// <summary>Whole question (all model calls and tools); the page waits at most this long.</summary>
    public int TimeoutSeconds { get; set; } = 90;
    /// <summary>Questions per user in any rolling minute / hour (in memory, per instance).</summary>
    public int PerUserPerMinute { get; set; } = 4;
    public int PerUserPerHour { get; set; } = 30;
    /// <summary>Questions answered at the same time on this instance; more get "busy" immediately.</summary>
    public int MaxConcurrent { get; set; } = 4;
    /// <summary>Effort when Ai:Features:QuestionAnswer:Effort is not set (Q&amp;A needs more than the global "low").</summary>
    public string Effort { get; set; } = "medium";
}
