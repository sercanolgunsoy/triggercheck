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
        return Transition(monitor, outcome.IsUp, checkedAtUtc);
    }

    /// <summary>
    /// True when <paramref name="outcome"/> is a failure that would take the monitor DOWN (the next failure reaches
    /// FailureThreshold), i.e. the runner should run a confirmation probe before applying it.
    /// </summary>
    public static bool NeedsConfirmation(MonitorEntity monitor, CheckOutcome outcome) =>
        !outcome.IsUp && IsActivelyMonitored(monitor) && monitor.Status != MonitorStatus.Down
        && monitor.ConsecutiveFailures + 1 >= Math.Max(1, monitor.FailureThreshold);

    /// <summary>
    /// Applies a failed probe (<paramref name="failure"/> at <paramref name="failedAtUtc"/>) together with its
    /// confirmation probe. Rules:
    /// <list type="bullet">
    /// <item>The schedule continues from the original probe time (the confirmation does not shift the interval).</item>
    /// <item>Confirmation failed: both probes count as consecutive failures; the monitor goes DOWN with
    /// StatusChangedAtUtc = <paramref name="failedAtUtc"/> (the outage started at the first failed probe).</item>
    /// <item>Confirmation succeeded: the failure is treated as transient. The confirmation counts as a successful
    /// check, so ConsecutiveFailures is reset to 0 exactly like after any successful check (Unknown becomes Up).
    /// The failed probe stays in the check history (and therefore in uptime %), but opens no incident.</item>
    /// </list>
    /// Last* fields describe the confirmation, the most recent probe.
    /// </summary>
    public static StatusTransition ApplyConfirmed(MonitorEntity monitor, CheckOutcome failure, DateTime failedAtUtc, CheckOutcome confirmation, DateTime confirmedAtUtc)
    {
        monitor.NextCheckAtUtc = NextCheckAfter(monitor.NextCheckAtUtc, monitor.IntervalSeconds, failedAtUtc);
        StatusTransition transition;
        if (confirmation.IsUp || failure.IsUp)
        {
            transition = Transition(monitor, true, confirmedAtUtc);
        }
        else
        {
            monitor.ConsecutiveFailures++; // the original failure; Transition counts the confirmation
            transition = Transition(monitor, false, failedAtUtc);
        }
        RecordProbe(monitor, confirmation, confirmedAtUtc);
        return transition;
    }

    private static StatusTransition Transition(MonitorEntity monitor, bool isUp, DateTime checkedAtUtc)
    {
        if (isUp)
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

    /// <summary>
    /// Stores the certificate seen by the probe. Probes over a reused connection carry no certificate; the last
    /// known one is then kept.
    /// </summary>
    public static void RecordCertificate(MonitorEntity monitor, CertificateInfo? certificate, DateTime checkedAtUtc)
    {
        if (certificate is null) return;
        monitor.CertExpiresAtUtc = certificate.NotAfterUtc;
        monitor.CertSubject = certificate.Subject;
        monitor.CertIssuer = certificate.Issuer;
        monitor.CertError = certificate.Error;
        monitor.CertCheckedAtUtc = checkedAtUtc;
    }

    /// <summary>Choices offered for <see cref="MonitorEntity.CertWarnDays"/> (0 = off).</summary>
    public static readonly int[] CertWarnDayChoices = [30, 14, 7, 0];

    /// <summary>
    /// True once per certificate when it is still valid but expires within CertWarnDays; marks it as warned
    /// (<see cref="MonitorEntity.CertWarningSentForUtc"/>). An already expired or otherwise invalid certificate makes
    /// the probe itself fail, so it is reported through the normal DOWN alert instead. A renewed certificate (new
    /// NotAfter) can warn again. Changing CertWarnDays does not re-warn for the same certificate.
    /// </summary>
    public static bool TryMarkCertificateWarning(MonitorEntity monitor, DateTime nowUtc)
    {
        if (monitor.CertWarnDays <= 0 || monitor.Type is not (MonitorType.Http or MonitorType.Keyword)) return false;
        if (monitor.CertExpiresAtUtc is not { } expires || monitor.CertError is not null) return false;
        var remaining = expires - nowUtc;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromDays(monitor.CertWarnDays)) return false;
        if (monitor.CertWarningSentForUtc == expires) return false;
        monitor.CertWarningSentForUtc = expires;
        return true;
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
