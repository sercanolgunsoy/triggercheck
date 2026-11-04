namespace TriggerCheck.Domain.Entities;

/// <summary>A downtime period. Opened when a monitor goes Down, closed (ResolvedAtUtc set) when it comes back Up.</summary>
public class Incident
{
    public int Id { get; set; }
    public int MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Cause { get; set; }

    /// <summary>The monitor recovered while it was in maintenance: the incident was closed without an UP alert.</summary>
    public bool ResolvedDuringMaintenance { get; set; }

    // ---- acknowledgement and escalation (FEAT-5) ----
    /// <summary>When a customer admin acknowledged the incident; acknowledged incidents are not escalated further.</summary>
    public DateTime? AcknowledgedAtUtc { get; set; }
    /// <summary>Identity user id of the admin who acknowledged it (no FK: the user may be deleted later).</summary>
    public string? AcknowledgedByUserId { get; set; }
    public string? AcknowledgeNote { get; set; }
    /// <summary>Last escalation step sent for this incident (0 = only the DOWN alert). Persisted so restarts never repeat a step.</summary>
    public int LastEscalationStep { get; set; }

    public bool IsOpen => ResolvedAtUtc == null;
}
