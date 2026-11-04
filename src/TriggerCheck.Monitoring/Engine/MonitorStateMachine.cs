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
    /// First half of confirm-before-alert: records the failed probe (taken at <paramref name="failedAtUtc"/>) without
    /// counting it, marks the monitor as waiting for a confirmation (bound to the current Version) and schedules the
    /// confirmation probe at <paramref name="confirmAtUtc"/> (probe end + delay).
    /// </summary>
    public static void BeginConfirmation(MonitorEntity monitor, CheckOutcome failure, DateTime failedAtUtc, DateTime confirmAtUtc)
    {
        RecordProbe(monitor, failure, failedAtUtc);
        monitor.PendingConfirmationSinceUtc = failedAtUtc;
        monitor.PendingConfirmationVersion = monitor.Version;
        monitor.NextCheckAtUtc = confirmAtUtc;
    }

    /// <summary>Slack on top of delay + interval + timeout before a pending confirmation is considered stale.</summary>
    public static readonly TimeSpan PendingConfirmationSlack = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A confirmation is pending, nobody changed the monitor since it was recorded, and it is recent enough: a
    /// confirmation run later than delay + interval + timeout (+ slack) after the failure (process down, backlog)
    /// would backdate an outage to an old failure, so the probe is then evaluated from scratch.
    /// </summary>
    public static bool HasValidPendingConfirmation(MonitorEntity monitor, DateTime nowUtc, TimeSpan delay) =>
        IsPendingForCurrentVersion(monitor) && nowUtc - monitor.PendingConfirmationSinceUtc!.Value <= MaxPendingAge(monitor, delay);

    /// <summary>How many max ages a late confirmation may be and still count as one (see <see cref="HasExpiredPendingConfirmation"/>).</summary>
    public const int LateConfirmationFactor = 3;

    /// <summary>
    /// The confirmation is late (older than <see cref="HasValidPendingConfirmation"/> allows, e.g. sustained scheduler
    /// lag) but not ancient: up to <see cref="LateConfirmationFactor"/> x the max age. A failing late probe then still
    /// confirms the outage (dated from that probe). Anything older (e.g. after a long restart) starts over.
    /// </summary>
    public static bool HasExpiredPendingConfirmation(MonitorEntity monitor, DateTime nowUtc, TimeSpan delay) =>
        IsPendingForCurrentVersion(monitor) && nowUtc - monitor.PendingConfirmationSinceUtc!.Value <= MaxPendingAge(monitor, delay) * LateConfirmationFactor;

    private static bool IsPendingForCurrentVersion(MonitorEntity monitor) =>
        monitor.PendingConfirmationSinceUtc is not null && monitor.PendingConfirmationVersion == monitor.Version;

    private static TimeSpan MaxPendingAge(MonitorEntity monitor, TimeSpan delay) =>
        delay + TimeSpan.FromSeconds(Math.Max(1, monitor.IntervalSeconds)) + TimeSpan.FromSeconds(Math.Max(1, monitor.TimeoutSeconds)) + PendingConfirmationSlack;

    public static void ClearPendingConfirmation(MonitorEntity monitor)
    {
        monitor.PendingConfirmationSinceUtc = null;
        monitor.PendingConfirmationVersion = null;
    }

    /// <summary>
    /// Second half of confirm-before-alert: applies the confirmation probe of a failure recorded at
    /// <paramref name="failedAtUtc"/>. Rules:
    /// <list type="bullet">
    /// <item>The schedule continues from the original failure (as if it had been the scheduled run), not from the
    /// confirmation.</item>
    /// <item>Confirmation failed: both probes count as consecutive failures; the monitor goes DOWN with
    /// StatusChangedAtUtc = <paramref name="failedAtUtc"/> (the outage started at the first failed probe).</item>
    /// <item>Confirmation succeeded: the failure is treated as transient. The confirmation counts as a successful
    /// check, so ConsecutiveFailures is reset to 0 exactly like after any successful check (Unknown becomes Up).
    /// The failed probe stays in the check history (and therefore in uptime %), but opens no incident.</item>
    /// </list>
    /// Last* fields describe the confirmation, the most recent probe.
    /// </summary>
    public static StatusTransition ApplyConfirmed(MonitorEntity monitor, DateTime failedAtUtc, CheckOutcome confirmation, DateTime confirmedAtUtc)
    {
        monitor.NextCheckAtUtc = NextCheckAfter(failedAtUtc, monitor.IntervalSeconds, confirmedAtUtc);
        StatusTransition transition;
        if (confirmation.IsUp)
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

    /// <summary>Days before expiry of the final reminder sent when the configured warning window is larger.</summary>
    public const int FinalCertReminderDays = 7;

    /// <summary>True for https Http/Keyword/JsonApi monitors, the only ones whose certificate is tracked.</summary>
    public static bool IsHttpsMonitor(MonitorEntity monitor) =>
        monitor.Type is MonitorType.Http or MonitorType.Keyword or MonitorType.JsonApi
        && monitor.Target.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The warning level (days) the certificate of <paramref name="monitor"/> has reached at <paramref name="nowUtc"/>,
    /// or null when no warning is due: CertWarnDays when it enters the configured window, and
    /// <see cref="FinalCertReminderDays"/> as a final reminder when the window is larger. Only for https monitors with
    /// a valid, not yet expired certificate (an expired or otherwise invalid certificate makes the probe itself fail,
    /// so it is reported through the normal DOWN alert).
    /// </summary>
    public static int? CertificateWarningLevel(MonitorEntity monitor, DateTime nowUtc)
    {
        if (monitor.CertWarnDays <= 0 || !IsHttpsMonitor(monitor)) return null;
        if (monitor.CertExpiresAtUtc is not { } expires || monitor.CertError is not null) return null;
        var remaining = expires - nowUtc;
        if (remaining <= TimeSpan.Zero) return null;
        if (monitor.CertWarnDays > FinalCertReminderDays && remaining <= TimeSpan.FromDays(FinalCertReminderDays)) return FinalCertReminderDays;
        return remaining <= TimeSpan.FromDays(monitor.CertWarnDays) ? monitor.CertWarnDays : null;
    }

    /// <summary>
    /// True when a "certificate expiring" alert is due, and marks it as sent: at most one alert per certificate and
    /// level (see <see cref="CertificateWarningLevel"/>), i.e. one when entering the window and one final reminder.
    /// A renewed certificate (new NotAfter) starts over. Changing CertWarnDays does not repeat a level already sent.
    /// </summary>
    public static bool TryMarkCertificateWarning(MonitorEntity monitor, DateTime nowUtc)
    {
        if (CertificateWarningLevel(monitor, nowUtc) is not { } level) return false;
        var expires = monitor.CertExpiresAtUtc!.Value;
        if (monitor.CertWarningSentForUtc == expires && monitor.CertWarningSentDays is { } sent && sent <= level) return false;
        monitor.CertWarningSentForUtc = expires;
        monitor.CertWarningSentDays = level;
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
