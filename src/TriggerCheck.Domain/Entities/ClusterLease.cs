namespace TriggerCheck.Domain.Entities;

/// <summary>
/// OPS-3: a named, time-bounded lease for a background job that must run on one instance at a time (leader election
/// per job). The holder renews it on every run; another instance takes over only after it expired.
/// </summary>
public class ClusterLease
{
    /// <summary>Job name, e.g. "billing-maintenance".</summary>
    public string Name { get; set; } = "";
    /// <summary>Instance id of the current holder.</summary>
    public string Owner { get; set; } = "";
    public DateTime LeasedUntilUtc { get; set; }
}
