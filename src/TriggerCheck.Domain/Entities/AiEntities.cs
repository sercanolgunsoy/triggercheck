namespace TriggerCheck.Domain.Entities;

/// <summary>Autonomy level of an AI output or action (see docs/BACKLOG.md, "Özerklik seviyeleri").</summary>
public enum AiAutonomy
{
    /// <summary>Information only: shown, nothing changes.</summary>
    A0 = 0,
    /// <summary>Suggestion: the user applies it with one click.</summary>
    A1 = 1,
    /// <summary>Approved action: AI prepares, a CustomerAdmin approves.</summary>
    A2 = 2,
    /// <summary>Limited automatic: within rule limits, reversible, logged, can be switched off by the customer.</summary>
    A3 = 3
}

public enum AiConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum AiDecisionStatus
{
    Succeeded = 0,
    /// <summary>The model declined (stop_reason "refusal").</summary>
    Refused = 1,
    /// <summary>Error, timeout, truncated (max_tokens) or invalid output. Details only in ErrorCode / logs.</summary>
    Failed = 2,
    /// <summary>Monthly AI budget of the customer's plan is used up.</summary>
    SkippedBudget = 3,
    /// <summary>AI is switched off globally (Ai:Enabled) or no API key is configured.</summary>
    SkippedDisabled = 4,
    /// <summary>The customer has not opted in (Customer.AiEnabled = false).</summary>
    SkippedOptOut = 5,
    /// <summary>The feature is not included in the customer's plan.</summary>
    SkippedPlan = 6
}

/// <summary>
/// One AI call (or skipped call) and its outcome. Written for every attempt, including failures and skips, so cost,
/// quality (feedback) and behaviour can be audited per tenant.
/// </summary>
public class AiDecision
{
    public long Id { get; set; }
    public int CustomerId { get; set; }
    /// <summary>Feature name, see <see cref="TriggerCheck.Domain.AiFeatures"/>.</summary>
    public string Feature { get; set; } = "";
    /// <summary>What the decision is about, e.g. "Incident" / "42".</summary>
    public string SubjectType { get; set; } = "";
    public string SubjectId { get; set; } = "";
    /// <summary>SHA-256 (hex) of the exact model input; with Feature + Subject the idempotency key.</summary>
    public string InputHash { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    /// <summary>Validated AiInterpretation JSON (null unless Succeeded).</summary>
    public string? OutputJson { get; set; }
    public AiConfidence? MaxConfidence { get; set; }
    /// <summary>Highest autonomy level among the validated suggested actions (A0 when there are none).</summary>
    public AiAutonomy AutonomyLevel { get; set; }
    public AiDecisionStatus Status { get; set; }
    /// <summary>Short machine code for failures (e.g. "timeout", "max_tokens", "rate_limited"); never an exception message.</summary>
    public string? ErrorCode { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// <summary>Estimated cost in USD from the configured per-model prices.</summary>
    public decimal CostEstimate { get; set; }
    public int LatencyMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>+1 (helpful), -1 (not helpful) or null (no feedback yet).</summary>
    public int? UserFeedback { get; set; }
    public string? FeedbackByUserId { get; set; }
    public DateTime? FeedbackAtUtc { get; set; }
}

/// <summary>
/// L1 statistical baseline of a monitor's response time (AI-1): robust median + MAD of successful checks over a rolling
/// window, per UTC hour-of-day bucket (0-23) plus a global bucket (<see cref="GlobalBucket"/>). Key: (MonitorId, Bucket).
/// </summary>
public class MonitorBaseline
{
    public const int GlobalBucket = -1;

    public int MonitorId { get; set; }
    public int Bucket { get; set; }
    public int CustomerId { get; set; }
    public double MedianMs { get; set; }
    public double MadMs { get; set; }
    public int SampleCount { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// An anomaly period detected by the L1 baseline job (AI-1), e.g. a slowdown. Open while <see cref="EndedAtUtc"/> is
/// null. Signals never change monitor status or incidents (L0 stays deterministic).
/// </summary>
public class AnomalySignal
{
    public long Id { get; set; }
    public int MonitorId { get; set; }
    public int CustomerId { get; set; }
    /// <summary>Metric name, e.g. "ResponseTimeMs".</summary>
    public string Metric { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    /// <summary>Highest robust z-score seen during the period.</summary>
    public double Score { get; set; }
    /// <summary>JSON with the evidence (baseline median/MAD, observed values, ratio).</summary>
    public string DetailsJson { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }

    public bool IsOpen => EndedAtUtc == null;
}
