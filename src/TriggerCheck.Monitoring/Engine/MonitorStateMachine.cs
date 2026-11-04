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
    /// <summary>Down -&gt; Up (or the first Up after resuming a monitor that was paused while Down). Close the open incident (if any) and send an UP alert.</summary>
    Recovered,
    /// <summary>Unknown/Up -&gt; Down after FailureThreshold consecutive failures. Open an incident and send a DOWN alert.</summary>
    WentDown
}

/// <summary>Pure status logic, kept free of I/O so it can be unit tested in isolation.</summary>
public static class MonitorStateMachine
{
    /// <summary>
    /// Applies a probe outcome to an actively monitored monitor: updates runtime fields, schedules the next check
    /// (see <see cref="NextCheckAfter"/>) and returns the resulting status transition.
    /// </summary>
    public static StatusTransition Apply(MonitorEntity monitor, CheckOutcome outcome, DateTime checkedAtUtc)
    {
        RecordProbe(monitor, outcome, checkedAtUtc);
        monitor.NextCheckAtUtc = NextCheckAfter(monitor.NextCheckAtUtc, monitor.IntervalSeconds, checkedAtUtc);

        if (outcome.IsUp)
        {
            monitor.ConsecutiveFailures = 0;
            if (monitor.Status == MonitorStatus.Up) return StatusTransition.None;
            var wasDown = monitor.Status == MonitorStatus.Down || monitor.DownWhenPaused;
            monitor.DownWhenPaused = false;
            SetStatus(monitor, MonitorStatus.Up, checkedAtUtc);
            return wasDown ? StatusTransition.Recovered : StatusTransition.BecameUp;
        }

        monitor.ConsecutiveFailures++;
        if (monitor.Status != MonitorStatus.Down && monitor.ConsecutiveFailures >= Math.Max(1, monitor.FailureThreshold))
        {
            monitor.DownWhenPaused = false;
            SetStatus(monitor, MonitorStatus.Down, checkedAtUtc);
            return StatusTransition.WentDown;
        }
        return StatusTransition.None;
    }

    /// <summary>
    /// Next scheduled check. Counted from the previously scheduled time (not from the actual run time, which is
    /// always a little late because of the scheduler tick and dispatch latency, so the schedule would drift by
    /// up to one tick per cycle). Falls back to <paramref name="checkedAtUtc"/> + interval when there was no
    /// schedule, when the run happened early (manual "check now") or when the schedule fell behind (first run,
    /// after resume, overload) - we realign instead of firing a burst of catch-up checks.
    /// </summary>
    public static DateTime NextCheckAfter(DateTime? scheduledUtc, int intervalSeconds, DateTime checkedAtUtc)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        if (scheduledUtc is not { } scheduled || scheduled > checkedAtUtc) return checkedAtUtc + interval;
        var next = scheduled + interval;
        return next > checkedAtUtc ? next : checkedAtUtc + interval;
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
