using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Engine;

public enum StatusTransition
{
    /// <summary>Status did not change.</summary>
    None,
    /// <summary>Unknown -&gt; Up (first successful check). No incident, no alert.</summary>
    BecameUp,
    /// <summary>Down -&gt; Up. Close the open incident and send an UP alert.</summary>
    Recovered,
    /// <summary>Unknown/Up -&gt; Down after FailureThreshold consecutive failures. Open an incident and send a DOWN alert.</summary>
    WentDown
}

/// <summary>Pure status logic, kept free of I/O so it can be unit tested in isolation.</summary>
public static class MonitorStateMachine
{
    /// <summary>
    /// Applies a probe outcome to an actively monitored monitor: updates runtime fields, schedules the next check
    /// (<paramref name="checkedAtUtc"/> + IntervalSeconds) and returns the resulting status transition.
    /// </summary>
    public static StatusTransition Apply(MonitorEntity monitor, CheckOutcome outcome, DateTime checkedAtUtc)
    {
        RecordProbe(monitor, outcome, checkedAtUtc);
        monitor.NextCheckAtUtc = checkedAtUtc.AddSeconds(Math.Max(1, monitor.IntervalSeconds));

        if (outcome.IsUp)
        {
            monitor.ConsecutiveFailures = 0;
            if (monitor.Status == MonitorStatus.Up) return StatusTransition.None;
            var previous = monitor.Status;
            SetStatus(monitor, MonitorStatus.Up, checkedAtUtc);
            return previous == MonitorStatus.Down ? StatusTransition.Recovered : StatusTransition.BecameUp;
        }

        monitor.ConsecutiveFailures++;
        if (monitor.Status != MonitorStatus.Down && monitor.ConsecutiveFailures >= Math.Max(1, monitor.FailureThreshold))
        {
            SetStatus(monitor, MonitorStatus.Down, checkedAtUtc);
            return StatusTransition.WentDown;
        }
        return StatusTransition.None;
    }

    /// <summary>
    /// Records the probe on the monitor without touching Status, ConsecutiveFailures or NextCheckAtUtc.
    /// Used for "check now" on paused/disabled monitors (and monitors of suspended customers).
    /// </summary>
    public static void RecordProbe(MonitorEntity monitor, CheckOutcome outcome, DateTime checkedAtUtc)
    {
        monitor.LastCheckedAtUtc = checkedAtUtc;
        monitor.LastResponseTimeMs = outcome.ResponseTimeMs;
        monitor.LastError = outcome.IsUp ? null : outcome.Error;
    }

    /// <summary>True if the scheduler would check this monitor (state machine applies only then).</summary>
    public static bool IsActivelyMonitored(MonitorEntity monitor) =>
        monitor.IsEnabled && monitor.Status != MonitorStatus.Paused && (monitor.Customer?.IsActive ?? true);

    private static void SetStatus(MonitorEntity monitor, MonitorStatus status, DateTime at)
    {
        monitor.Status = status;
        monitor.StatusChangedAtUtc = at;
    }
}
