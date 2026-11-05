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
    /// <summary>Longest time range of monitor_stats over all monitors (no monitor_id); one monitor may use MaxRangeDays.</summary>
    public int MaxAllMonitorsRangeDays { get; set; } = 7;
    /// <summary>Incident rows read for the downtime figures of one monitor_stats call (newest first; the result says when cut).</summary>
    public int MaxStatsIncidents { get; set; } = 20_000;
    /// <summary>Earlier questions and answers of the page session sent along for follow-up questions.</summary>
    public int MaxHistoryTurns { get; set; } = 3;
    /// <summary>Characters of each earlier answer sent along.</summary>
    public int MaxHistoryAnswerChars { get; set; } = 1500;
    /// <summary>
    /// Output token cap of one model call (thinking + tool call or answer) unless Ai:Features:QuestionAnswer:MaxTokens is
    /// set. Tool turns and the short plain-text answer (about 120 words) need far less; the cap bounds the worst case
    /// (MaxIterations x MaxTokensPerCall output tokens per question). A call that hits it is recorded as Truncated.
    /// </summary>
    public int MaxTokensPerCall { get; set; } = 4000;
    /// <summary>
    /// Whole question (all model calls and tools), measured on the server: a closed tab or proxy timeout does not
    /// abort a call in flight (its usage is still recorded). Capped at 2 x Ai:TimeoutSeconds - 5 so the Pending sweep never
    /// closes a running question.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 90;
    /// <summary>
    /// Per HTTP attempt of one model call (own value instead of the interpreter's Ai:TimeoutSeconds / (MaxRetries + 1),
    /// which is sized for short low-effort calls). A Q&amp;A turn at medium effort can take tens of seconds.
    /// </summary>
    public int CallTimeoutSeconds { get; set; } = 60;
    /// <summary>SDK retries of one model call (0 or 1; 408/409/429/5xx and connection errors). The question deadline still applies.</summary>
    public int MaxRetries { get; set; } = 1;
    /// <summary>Questions per user in any rolling minute / hour (in memory, per instance).</summary>
    public int PerUserPerMinute { get; set; } = 4;
    public int PerUserPerHour { get; set; } = 30;
    /// <summary>Questions answered at the same time on this instance; more get "busy" immediately.</summary>
    public int MaxConcurrent { get; set; } = 4;
    /// <summary>Effort when Ai:Features:QuestionAnswer:Effort is not set (Q&amp;A needs more than the global "low").</summary>
    public string Effort { get; set; } = "medium";
}
