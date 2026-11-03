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

    public bool IsOpen => ResolvedAtUtc == null;
}
