namespace TriggerCheck.Monitoring;

/// <summary>
/// Runs one check for a monitor immediately (used by the scheduler and by the "Check now" button in the UI),
/// persists the CheckResult and applies status/incident/alert transitions.
/// </summary>
public interface IMonitorCheckRunner
{
    Task<Domain.Entities.CheckResult> RunAsync(int monitorId, CancellationToken ct = default);
}
