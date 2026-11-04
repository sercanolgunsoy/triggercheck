using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Engine;

/// <summary>
/// Runs one probe and persists everything that follows from it. Behavior by monitor state:
/// <list type="bullet">
/// <item>Actively monitored (IsEnabled, not Paused, customer active): full state machine - CheckResult, runtime
/// fields, next schedule (<see cref="MonitorStateMachine.NextCheckAfter"/>), status transitions, incident
/// open/close, DOWN/UP alerts.</item>
/// <item>Paused / disabled / customer suspended ("check now" from the UI): the probe runs and a CheckResult plus
/// LastCheckedAtUtc / LastResponseTimeMs / LastError are recorded, but Status, ConsecutiveFailures, NextCheckAtUtc,
/// incidents and alerts are left untouched. A paused monitor therefore stays Paused.</item>
/// </list>
/// Concurrency with the UI: the probe (slow) runs against an untracked snapshot. Afterwards the monitor is
/// re-read and only runtime fields are written, guarded by <see cref="MonitorEntity.Version"/>:
/// <list type="bullet">
/// <item>deleted meanwhile, or its <see cref="ProbeSettings"/> were edited: the stale result is discarded and
/// <see cref="MonitorCheckDiscardedException"/> is thrown;</item>
/// <item>Status / IsEnabled differ from the snapshot (paused, resumed, disabled meanwhile) or the monitor is not
/// actively monitored: only the probe is recorded - never a transition, incident or alert;</item>
/// <item>a user change lands between the re-read and the save: the save fails on the token and is retried
/// against the fresh row.</item>
/// </list>
/// The runner bumps Version itself whenever it changes Status or opens/closes an incident, so a concurrent
/// pause/resume/edit that read the old state fails on the token and retries on the new state.
/// Alert delivery happens after the state is saved and its failures are only logged.
/// <para>
/// Confirm before alert (<see cref="MonitoringOptions.ConfirmBeforeDown"/>): when a failed probe would take an
/// actively monitored monitor DOWN (<see cref="MonitorStateMachine.NeedsConfirmation"/>), the runner waits
/// <see cref="MonitoringOptions.ConfirmationDelaySeconds"/> (via <see cref="TimeProvider"/>) and probes once more,
/// still holding the per-monitor gate. Before the confirmation probe the monitor is re-read: deleted or probe
/// settings edited =&gt; the result is discarded as above; paused/disabled/resumed meanwhile =&gt; no confirmation and
/// the failure is only recorded (no transition, incident or alert). Both probes are then saved together under the
/// same Version rules; see <see cref="MonitorStateMachine.ApplyConfirmed"/> for how they are counted. Shutdown during
/// the delay drops the pending result like an interrupted probe. Worst-case DOWN detection: interval + delay + timeout.
/// </para>
/// <para>
/// TLS certificates (https Http/Keyword): the certificate captured by the probe is stored on the monitor, and a
/// one-time "certificate expiring" alert is sent when it is within <see cref="MonitorEntity.CertWarnDays"/> of expiry
/// (<see cref="MonitorStateMachine.TryMarkCertificateWarning"/>), for actively monitored monitors only.
/// </para>
/// </summary>
public sealed class MonitorCheckRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IMonitorChecker> checkers,
    IAlertDispatcher alerts,
    MonitorRunGate gate,
    TimeProvider time,
    IOptionsMonitor<MonitoringOptions> options,
    ILogger<MonitorCheckRunner> logger) : IMonitorCheckRunner
{
    private const int MaxSaveAttempts = 5;

    public async Task<CheckResult> RunAsync(int monitorId, CancellationToken ct = default)
    {
        CheckResult result;
        AlertMessage? alert;
        AlertMessage? certAlert;

        using (await gate.AcquireAsync(monitorId, ct))
        {
            MonitorEntity snapshot;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                snapshot = await db.Monitors.AsNoTracking().Include(m => m.Customer).SingleOrDefaultAsync(m => m.Id == monitorId, ct)
                    ?? throw new KeyNotFoundException($"Monitor {monitorId} not found.");
            }

            var checkedAt = time.GetUtcNow().UtcDateTime;
            var outcome = await ProbeAsync(snapshot, ct);
            var confirmation = options.CurrentValue.ConfirmBeforeDown && MonitorStateMachine.NeedsConfirmation(snapshot, outcome)
                ? await ConfirmAsync(snapshot, ct)
                : null;

            // State must be persisted even if shutdown was requested after the probe completed.
            for (var attempt = 1; ; attempt++)
            {
                var first = ToResult(monitorId, outcome, checkedAt);
                var confirmed = confirmation is { } c ? ToResult(monitorId, c.Outcome, c.At) : null;
                result = confirmed ?? first;
                try
                {
                    (alert, certAlert) = await PersistAsync(snapshot, outcome, first, checkedAt, confirmation, confirmed);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
                {
                    logger.LogDebug("Monitor {MonitorId} changed while its check was being saved; retrying", monitorId);
                }
                catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
                {
                    // Deleted between our re-read and our save: the CheckResult insert hit the foreign key.
                    if (await MonitorExistsAsync(monitorId)) throw;
                    logger.LogDebug("Monitor {MonitorId} was deleted while its check was being saved; result discarded", monitorId);
                    throw new MonitorCheckDiscardedException(monitorId, monitorDeleted: true);
                }
            }
        }

        if (alert is not null)
        {
            try
            {
                await alerts.DispatchAsync(alert, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Alert dispatch failed for monitor {MonitorId}", monitorId);
            }
        }

        if (certAlert is not null)
        {
            try
            {
                await alerts.DispatchAsync(certAlert, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Certificate alert dispatch failed for monitor {MonitorId}", monitorId);
            }
        }

        return result;
    }

    private static CheckResult ToResult(int monitorId, CheckOutcome outcome, DateTime checkedAt) => new()
    {
        MonitorId = monitorId,
        CheckedAtUtc = checkedAt,
        IsUp = outcome.IsUp,
        StatusCode = outcome.StatusCode,
        ResponseTimeMs = outcome.ResponseTimeMs,
        Error = outcome.Error,
        DnsMs = outcome.Timings?.DnsMs,
        ConnectMs = outcome.Timings?.ConnectMs,
        TlsMs = outcome.Timings?.TlsMs,
        TtfbMs = outcome.Timings?.TtfbMs
    };

    private sealed record Confirmation(CheckOutcome Outcome, DateTime At);

    /// <summary>
    /// Waits the confirmation delay and probes again. Returns null (no confirmation; the failure is then only
    /// recorded by <see cref="PersistAsync"/>) when the monitor was paused/disabled/resumed meanwhile; throws
    /// <see cref="MonitorCheckDiscardedException"/> when it was deleted or its probe settings were edited.
    /// </summary>
    private async Task<Confirmation?> ConfirmAsync(MonitorEntity snapshot, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.ConfirmationDelaySeconds, 0, 60));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, time, ct);

        MonitorEntity? current;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            current = await db.Monitors.AsNoTracking().Include(m => m.Customer).SingleOrDefaultAsync(m => m.Id == snapshot.Id, ct);
        }
        if (current is null)
        {
            logger.LogDebug("Monitor {MonitorId} was deleted before its confirmation probe; result discarded", snapshot.Id);
            throw new MonitorCheckDiscardedException(snapshot.Id, monitorDeleted: true);
        }
        if (ProbeSettings.Of(current) != ProbeSettings.Of(snapshot))
        {
            logger.LogDebug("Monitor {MonitorId} was edited before its confirmation probe; stale result discarded", snapshot.Id);
            throw new MonitorCheckDiscardedException(snapshot.Id, monitorDeleted: false);
        }
        if (current.Status != snapshot.Status || current.IsEnabled != snapshot.IsEnabled || !MonitorStateMachine.IsActivelyMonitored(current))
        {
            logger.LogDebug("Monitor {MonitorId} was paused or changed before its confirmation probe; no confirmation", snapshot.Id);
            return null;
        }

        var at = time.GetUtcNow().UtcDateTime;
        var outcome = await ProbeAsync(snapshot, ct);
        logger.LogInformation("Monitor {MonitorId} confirmation probe: {Result}", snapshot.Id, outcome.IsUp ? "up (transient failure)" : "failed");
        return new Confirmation(outcome, at);
    }

    private async Task<bool> MonitorExistsAsync(int monitorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return await db.Monitors.AnyAsync(m => m.Id == monitorId);
    }

    /// <summary>Re-reads the monitor and applies the outcome (and its confirmation) to it. Returns the alerts to send, if any.</summary>
    private async Task<(AlertMessage? Status, AlertMessage? Certificate)> PersistAsync(MonitorEntity snapshot, CheckOutcome outcome, CheckResult result,
        DateTime checkedAt, Confirmation? confirmation, CheckResult? confirmationResult)
    {
        var ct = CancellationToken.None;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var monitor = await db.Monitors.Include(m => m.Customer).SingleOrDefaultAsync(m => m.Id == snapshot.Id, ct);
        if (monitor is null)
        {
            logger.LogDebug("Monitor {MonitorId} was deleted during its check; result discarded", snapshot.Id);
            throw new MonitorCheckDiscardedException(snapshot.Id, monitorDeleted: true);
        }
        if (ProbeSettings.Of(monitor) != ProbeSettings.Of(snapshot))
        {
            logger.LogDebug("Monitor {MonitorId} was edited during its check; stale result discarded", snapshot.Id);
            throw new MonitorCheckDiscardedException(snapshot.Id, monitorDeleted: false);
        }

        db.CheckResults.Add(result);
        if (confirmationResult is not null) db.CheckResults.Add(confirmationResult);
        AlertMessage? alert = null;
        AlertMessage? certAlert = null;
        var latest = confirmation?.Outcome ?? outcome;
        var latestAt = confirmation?.At ?? checkedAt;
        MonitorStateMachine.RecordCertificate(monitor, latest.Certificate ?? outcome.Certificate, latestAt);
        var unchangedLifecycle = monitor.Status == snapshot.Status && monitor.IsEnabled == snapshot.IsEnabled;
        if (unchangedLifecycle && MonitorStateMachine.IsActivelyMonitored(snapshot) && MonitorStateMachine.IsActivelyMonitored(monitor))
        {
            var transition = confirmation is null
                ? MonitorStateMachine.Apply(monitor, outcome, checkedAt)
                : MonitorStateMachine.ApplyConfirmed(monitor, outcome, checkedAt, confirmation.Outcome, confirmation.At);
            // A confirmed outage started at the first failed probe.
            var transitionAt = transition == StatusTransition.WentDown ? checkedAt : latestAt;
            alert = await ApplyTransitionAsync(db, monitor, transition, transitionAt, ct);
            // Status changed (and possibly an incident opened/closed): make concurrent user writers retry.
            if (transition != StatusTransition.None) monitor.Version++;
            if (MonitorStateMachine.TryMarkCertificateWarning(monitor, latestAt))
                certAlert = AlertMessage.CertificateExpiring(monitor, latestAt);
        }
        else
        {
            MonitorStateMachine.RecordProbe(monitor, outcome, checkedAt);
            if (confirmation is not null) MonitorStateMachine.RecordProbe(monitor, confirmation.Outcome, confirmation.At);
            logger.LogDebug("Check of inactive monitor {MonitorId} recorded without state change: {IsUp}", monitor.Id, latest.IsUp);
        }

        await db.SaveChangesAsync(ct);
        return (alert, certAlert);
    }

    private async Task<CheckOutcome> ProbeAsync(MonitorEntity monitor, CancellationToken ct)
    {
        var checker = checkers.FirstOrDefault(c => c.Type == monitor.Type);
        if (checker is null) return CheckOutcome.Down($"Monitor type {monitor.Type} is not supported");
        try
        {
            return await checker.CheckAsync(monitor, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Checkers should not throw; this is a safety net against bugs.
            logger.LogError(ex, "Checker {Checker} threw for monitor {MonitorId}", checker.GetType().Name, monitor.Id);
            // The message is customer-visible: keep exception details in the log only.
            return CheckOutcome.Down("Internal checker error");
        }
    }

    private async Task<AlertMessage?> ApplyTransitionAsync(AppDbContext db, MonitorEntity monitor, StatusTransition transition, DateTime now, CancellationToken ct)
    {
        switch (transition)
        {
            case StatusTransition.WentDown:
            {
                var alreadyOpen = await db.Incidents.AnyAsync(i => i.MonitorId == monitor.Id && i.ResolvedAtUtc == null, ct);
                if (!alreadyOpen)
                    db.Incidents.Add(new Incident { MonitorId = monitor.Id, StartedAtUtc = now, Cause = monitor.LastError });
                logger.LogWarning("Monitor {MonitorId} '{Name}' is DOWN after {Failures} consecutive failures: {Error}",
                    monitor.Id, monitor.Name, monitor.ConsecutiveFailures, monitor.LastError);
                return AlertMessage.For(monitor, AlertKind.Down, now);
            }
            case StatusTransition.Recovered:
            {
                var open = await db.Incidents.Where(i => i.MonitorId == monitor.Id && i.ResolvedAtUtc == null).ToListAsync(ct);
                foreach (var incident in open) incident.ResolvedAtUtc = now;
                TimeSpan? downtime = open.Count > 0 ? now - open.Min(i => i.StartedAtUtc) : null;
                logger.LogInformation("Monitor {MonitorId} '{Name}' is UP again (downtime {Downtime})", monitor.Id, monitor.Name, downtime);
                return AlertMessage.For(monitor, AlertKind.Up, now, downtime);
            }
            case StatusTransition.BecameUp:
                logger.LogInformation("Monitor {MonitorId} '{Name}' is UP", monitor.Id, monitor.Name);
                return null;
            default:
                return null;
        }
    }
}
