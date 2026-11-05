namespace TriggerCheck.Domain.Entities;

public enum ReportPeriodKind
{
    Monthly = 0,
    Weekly = 1
}

public enum ReportNarrativeStatus
{
    /// <summary>Waiting for the batch job (monthly: created by the job; weekly / older months: requested in the panel).</summary>
    Requested = 0,
    /// <summary>Claimed by one app instance; the AI call is in progress.</summary>
    Running = 1,
    /// <summary>A validated narrative exists (<see cref="ReportNarrative.DecisionId"/>).</summary>
    Ready = 2,
    /// <summary>The call failed (timeout, refusal, invalid or ungrounded output); retried up to the attempt limit.</summary>
    Failed = 3,
    /// <summary>Not generated: opt-out, plan, budget or nothing to report. Final for this period.</summary>
    Skipped = 4
}

/// <summary>
/// AI-6: the AI narrative of one SLA report period of one customer ("what happened, what changed, what to watch").
/// Unique per (CustomerId, Kind, PeriodKey): the row is the cross-instance claim of the batch job, so a period is never
/// generated twice in parallel. The narrative itself is the linked <see cref="AiDecision"/>.
/// </summary>
public class ReportNarrative
{
    public long Id { get; set; }
    public int CustomerId { get; set; }
    public ReportPeriodKind Kind { get; set; }
    /// <summary>"yyyy-MM" (monthly) or ISO week "yyyy-Www" (weekly).</summary>
    public string PeriodKey { get; set; } = "";
    /// <summary>Report time zone the period was cut in (a change makes the row stale and it is regenerated).</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>"en" / "tr" (customer's AI language at the time of generation).</summary>
    public string Language { get; set; } = "en";
    public ReportNarrativeStatus Status { get; set; }
    public long? DecisionId { get; set; }
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>When the current / last attempt was claimed.</summary>
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
