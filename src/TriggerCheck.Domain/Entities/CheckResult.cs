namespace TriggerCheck.Domain.Entities;

/// <summary>One single probe execution.</summary>
public class CheckResult
{
    public long Id { get; set; }
    public int MonitorId { get; set; }
    public Monitor Monitor { get; set; } = null!;
    public DateTime CheckedAtUtc { get; set; }
    public bool IsUp { get; set; }
    public int? StatusCode { get; set; }
    public int ResponseTimeMs { get; set; }
    public string? Error { get; set; }
}
