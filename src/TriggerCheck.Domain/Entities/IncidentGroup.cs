namespace TriggerCheck.Domain.Entities;

/// <summary>Life cycle of an <see cref="IncidentGroup"/> (AI-4).</summary>
public enum IncidentGroupStatus
{
    /// <summary>A1: proposed to the customer ("group these incidents?"), not decided yet.</summary>
    Proposed = 0,
    /// <summary>A1: a CustomerAdmin accepted the proposal (with the members they kept).</summary>
    Accepted = 1,
    /// <summary>A1: a CustomerAdmin rejected the proposal.</summary>
    Rejected = 2,
    /// <summary>A1: nobody decided within the expiry time; not counted in the acceptance rate.</summary>
    Expired = 3,
    /// <summary>A3: notifications of the members are being grouped automatically.</summary>
    Auto = 4,
    /// <summary>A3: ungrouped (primary recovered, or a CustomerAdmin undid it); members got their own notifications.</summary>
    Dissolved = 5,
}

public enum IncidentGroupRole
{
    /// <summary>The first incident of the group; its notification is the group's notification.</summary>
    Primary = 0,
    /// <summary>Linked to the primary by a deterministic L1 rule (same host / IP with compatible errors).</summary>
    Rule = 1,
    /// <summary>Shares a weaker signal (same error type, related domain); grouped only after the LLM or the user says so.</summary>
    Ambiguous = 2,
}

/// <summary>
/// AI-4 alert correlation: incidents of one customer that started within a short window and share signals, grouped as
/// "primary incident + affected" for notification purposes only. A group never opens, closes or changes an incident
/// (L0 stays deterministic); deleting a group or a member never touches the incidents.
/// </summary>
public class IncidentGroup
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    /// <summary>The primary incident (also a member with <see cref="IncidentGroupRole.Primary"/>). No FK: display only.</summary>
    public int PrimaryIncidentId { get; set; }
    public IncidentGroupStatus Status { get; set; }
    /// <summary>A1 (proposal) or A3 (automatic notification grouping).</summary>
    public AiAutonomy Mode { get; set; }
    /// <summary>Comma separated L1 signal codes shared by the rule members, e.g. "same_host,same_error_type".</summary>
    public string Signals { get; set; } = "";
    /// <summary>Shared host (or IP) when the group was formed on it; display only.</summary>
    public string? Host { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    /// <summary>Identity user id of the admin who accepted / rejected / ungrouped it (no FK).</summary>
    public string? DecidedByUserId { get; set; }
    /// <summary>
    /// When the AI explanation job was queued for the current members (null = not yet, or members changed since).
    /// Set once per membership, so a group the model cannot explain is not queued again on every scan.
    /// </summary>
    public DateTime? ExplanationQueuedAtUtc { get; set; }

    public List<IncidentGroupMember> Members { get; set; } = [];
}

/// <summary>One incident in an <see cref="IncidentGroup"/>. An incident belongs to at most one group (unique index).</summary>
public class IncidentGroupMember
{
    public long Id { get; set; }
    public int GroupId { get; set; }
    public IncidentGroup Group { get; set; } = null!;
    public int IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public int CustomerId { get; set; }
    public IncidentGroupRole Role { get; set; }
    /// <summary>
    /// Part of the group: rule members and the primary from the start; ambiguous ones only after the user (A1) kept
    /// them. A member left out keeps its own notifications.
    /// </summary>
    public bool Included { get; set; }
    /// <summary>A3: its DOWN notification was folded into the primary's (null = it was notified on its own).</summary>
    public DateTime? FoldedAtUtc { get; set; }
    /// <summary>
    /// A3: when the folded DOWN was handed to the alert queue on its own after all (group undone, fold cap reached).
    /// Set only after a successful enqueue; the watcher re-queues folded, still-open members where it is null and the
    /// fold no longer applies, so a held-back DOWN cannot be lost.
    /// </summary>
    public DateTime? NotifiedAtUtc { get; set; }
    /// <summary>A3: when the member was listed in an "also affected" follow-up of its group (null = not yet).</summary>
    public DateTime? ReportedAtUtc { get; set; }
}
