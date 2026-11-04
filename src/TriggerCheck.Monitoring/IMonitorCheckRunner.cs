namespace TriggerCheck.Monitoring;

/// <summary>
/// Runs one check for a monitor immediately (used by the scheduler and by the "Check now" button in the UI),
/// persists the CheckResult and applies status/incident/alert transitions.
/// </summary>
public interface IMonitorCheckRunner
{
    Task<Domain.Entities.CheckResult> RunAsync(int monitorId, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="IMonitorCheckRunner.RunAsync"/> when the probe finished but its result was discarded because
/// the monitor was deleted (<see cref="MonitorDeleted"/>) or its probe settings were edited while the probe ran.
/// </summary>
public sealed class MonitorCheckDiscardedException(int monitorId, bool monitorDeleted)
    : Exception(monitorDeleted
        ? $"Monitor {monitorId} was deleted during its check; the result was discarded."
        : $"Monitor {monitorId} was edited during its check; the result was discarded.")
{
    public int MonitorId { get; } = monitorId;
    public bool MonitorDeleted { get; } = monitorDeleted;
}
