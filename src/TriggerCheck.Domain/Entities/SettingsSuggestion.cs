namespace TriggerCheck.Domain.Entities;

/// <summary>What an AI-5 settings suggestion proposes. Each kind is detected by one L1 rule on the monitor's history.</summary>
public enum SettingsSuggestionKind
{
    /// <summary>Most incidents recovered at the next check (flapping): raise <see cref="Monitor.FailureThreshold"/>.</summary>
    RaiseFailureThreshold = 0,
    /// <summary>Successful responses close to the timeout (plus timeouts): raise <see cref="Monitor.TimeoutSeconds"/>.</summary>
    RaiseTimeout = 1,
    /// <summary>The monitor has never had a successful check since it was created: the target may be wrong.</summary>
    TargetMayBeWrong = 2,
    /// <summary>Keyword monitor: the page responds but the keyword is often not found; review the keyword.</summary>
    ReviewKeyword = 3
}

public enum SettingsSuggestionStatus
{
    Open = 0,
    /// <summary>A CustomerAdmin applied it (through the normal monitor validation).</summary>
    Applied = 1,
    /// <summary>A CustomerAdmin dismissed it: the same kind is not suggested again for the monitor for 30 days.</summary>
    Dismissed = 2,
    /// <summary>Closed by the suggestion job: the condition is gone, the setting changed or a fresher suggestion replaced it.</summary>
    Obsolete = 3
}

/// <summary>
/// AI-5 settings suggestion (autonomy A1: shown to the customer, applied only by a CustomerAdmin's click). Detection is
/// rule based on the monitor's stored history (L1); every number shown as evidence is in <see cref="EvidenceJson"/>,
/// computed from the database. The LLM only explains it (an <see cref="AiDecision"/> with SubjectType
/// "SettingsSuggestion" and SubjectId = <see cref="Id"/>).
/// </summary>
public class SettingsSuggestion
{
    public long Id { get; set; }
    public int CustomerId { get; set; }
    public int MonitorId { get; set; }
    public SettingsSuggestionKind Kind { get; set; }
    public SettingsSuggestionStatus Status { get; set; }
    /// <summary>The setting's value when the suggestion was made (invariant text); Apply refuses when it changed since.</summary>
    public string? CurrentValue { get; set; }
    /// <summary>Proposed new value (invariant text); null when there is no concrete value (review target / keyword).</summary>
    public string? SuggestedValue { get; set; }
    /// <summary>Evidence facts computed by the rule: a JSON array of {"key","value"} (numbers in invariant format).</summary>
    public string EvidenceJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>When it was applied, dismissed or became obsolete.</summary>
    public DateTime? ClosedAtUtc { get; set; }
    /// <summary>Identity user id of the admin who applied / dismissed it (no FK: the user may be deleted later).</summary>
    public string? ClosedByUserId { get; set; }
}
