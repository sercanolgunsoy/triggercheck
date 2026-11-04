namespace TriggerCheck.Domain.Entities;

/// <summary>
/// Optional escalation policy of a customer (FEAT-5, at most one per customer). The normal DOWN alert is step 0; each
/// <see cref="EscalationStep"/> notifies its channels when an incident is still open and unacknowledged
/// <see cref="EscalationStep.DelayMinutes"/> after it started.
/// </summary>
public class EscalationPolicy
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public bool IsEnabled { get; set; } = true;
    public ICollection<EscalationStep> Steps { get; set; } = new List<EscalationStep>();
}

/// <summary>One escalation level: "after N minutes unacknowledged and still DOWN, notify these channels".</summary>
public class EscalationStep
{
    public int Id { get; set; }
    public int EscalationPolicyId { get; set; }
    public EscalationPolicy Policy { get; set; } = null!;
    /// <summary>Minutes after the incident started. Strictly increasing over the steps of a policy.</summary>
    public int DelayMinutes { get; set; }
    public ICollection<AlertChannel> Channels { get; set; } = new List<AlertChannel>();
}
