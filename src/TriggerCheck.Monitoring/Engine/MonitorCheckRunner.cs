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
/// Confirm before alert (<see cref="MonitoringOptions.ConfirmBeforeDown"/>): a failed probe that would take an
/// actively monitored monitor DOWN (<see cref="MonitorStateMachine.NeedsConfirmation"/>) is only recorded: the
/// CheckResult and Last* fields are saved, the failure is not counted yet, and the monitor is marked
/// <see cref="MonitorEntity.PendingConfirmationSinceUtc"/> with its next check moved to now +
/// <see cref="MonitoringOptions.ConfirmationDelaySeconds"/>. The run then returns, so neither a scheduler slot nor a
/// "check now" request waits for the delay. The next run (normally the scheduler picking the monitor up as due) is
/// the confirmation probe: <see cref="MonitorStateMachine.ApplyConfirmed"/> applies both probes (outage dated from
/// the first failure; a successful confirmation is a transient blip). The confirmation's CheckResult is flagged
/// <see cref="CheckResult.IsConfirmation"/> and excluded from uptime / chart statistics.
/// A pending confirmation is bound to the monitor's Version when it was recorded: any user change meanwhile (edit,
/// pause, resume, SuperAdmin clamp; delete removes the row) cancels it, and the next probe is evaluated from scratch.
/// Worst-case DOWN detection: interval + delay + scheduler tick + probe timeout.
/// </para>
/// <para>
/// TLS certificates (https Http/Keyword): the certificate captured by the probe is stored on the monitor, and a
/// "certificate expiring" alert is sent once when it enters <see cref="MonitorEntity.CertWarnDays"/> and once more at 7 days
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

            // State must be persisted even if shutdown was requested after the probe completed.
            for (var attempt = 1; ; attempt++)
            {
                result = ToResult(monitorId, outcome, checkedAt);
                try
                {
                    (alert, certAlert) = await PersistAsync(snapshot, outcome, result, checkedAt);
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

    private async Task<bool> MonitorExistsAsync(int monitorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return await db.Monitors.AnyAsync(m => m.Id == monitorId);
    }

    /// <summary>Re-reads the monitor and applies the outcome to it. Returns the alerts to send, if any.</summary>
    private async Task<(AlertMessage? Status, AlertMessage? Certificate)> PersistAsync(MonitorEntity snapshot, CheckOutcome outcome, CheckResult result, DateTime checkedAt)
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

        // Retries start from a fresh row: reset what an earlier attempt may have set on the (new) result object.
        result.IsConfirmation = false;
        db.CheckResults.Add(result);
        AlertMessage? alert = null;
        AlertMessage? certAlert = null;
        MonitorStateMachine.RecordCertificate(monitor, outcome.Certificate, checkedAt);
        var unchangedLifecycle = monitor.Status == snapshot.Status && monitor.IsEnabled == snapshot.IsEnabled;
        if (unchangedLifecycle && MonitorStateMachine.IsActivelyMonitored(snapshot) && MonitorStateMachine.IsActivelyMonitored(monitor))
        {
            var o = options.CurrentValue;
            var transition = StatusTransition.None;
            var transitionAt = checkedAt;
            if (MonitorStateMachine.HasValidPendingConfirmation(monitor))
            {
                // This run is the confirmation probe of an earlier failure.
                var failedAt = monitor.PendingConfirmationSinceUtc!.Value;
                result.IsConfirmation = true;
                MonitorStateMachine.ClearPendingConfirmation(monitor);
                transition = MonitorStateMachine.ApplyConfirmed(monitor, failedAt, outcome, checkedAt);
                if (transition == StatusTransition.WentDown) transitionAt = failedAt; // the outage started at the first failure
                logger.LogInformation("Monitor {MonitorId} confirmation probe: {Result}", monitor.Id, outcome.IsUp ? "up (transient failure)" : "failed");
            }
            else
            {
                MonitorStateMachine.ClearPendingConfirmation(monitor); // stale (monitor changed since) or none
                if (o.ConfirmBeforeDown && MonitorStateMachine.NeedsConfirmation(monitor, outcome))
                    MonitorStateMachine.BeginConfirmation(monitor, outcome, checkedAt, TimeSpan.FromSeconds(Math.Clamp(o.ConfirmationDelaySeconds, 0, 300)));
                else
                    transition = MonitorStateMachine.Apply(monitor, outcome, checkedAt);
            }
            alert = await ApplyTransitionAsync(db, monitor, transition, transitionAt, ct);
            // Status changed (and possibly an incident opened/closed): make concurrent user writers retry.
            if (transition != StatusTransition.None) monitor.Version++;
            if (MonitorStateMachine.TryMarkCertificateWarning(monitor, checkedAt))
                certAlert = AlertMessage.CertificateExpiring(monitor, checkedAt);
        }
        else
        {
            MonitorStateMachine.RecordProbe(monitor, outcome, checkedAt);
            logger.LogDebug("Check of inactive monitor {MonitorId} recorded without state change: {IsUp}", monitor.Id, outcome.IsUp);
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
