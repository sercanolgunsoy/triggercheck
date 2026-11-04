using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
/// Alerts are queued after the state is saved (<see cref="IAlertQueue"/>) and delivered by AlertSenderService, so a
/// slow notification target never holds the check slot.
/// </summary>
public sealed class MonitorCheckRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IMonitorChecker> checkers,
    IAlertQueue alerts,
    MonitorRunGate gate,
    TimeProvider time,
    ILogger<MonitorCheckRunner> logger) : IMonitorCheckRunner
{
    private const int MaxSaveAttempts = 5;

    public async Task<CheckResult> RunAsync(int monitorId, CancellationToken ct = default)
    {
        CheckResult result;
        AlertMessage? alert;

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
                result = new CheckResult
                {
                    MonitorId = monitorId,
                    CheckedAtUtc = checkedAt,
                    IsUp = outcome.IsUp,
                    StatusCode = outcome.StatusCode,
                    ResponseTimeMs = outcome.ResponseTimeMs,
                    Error = outcome.Error
                };
                try
                {
                    alert = await PersistAsync(snapshot, outcome, result, checkedAt);
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

        if (alert is not null) alerts.TryEnqueue(alert);

        return result;
    }

    private async Task<bool> MonitorExistsAsync(int monitorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return await db.Monitors.AnyAsync(m => m.Id == monitorId);
    }

    /// <summary>Re-reads the monitor and applies the outcome to it. Returns the alert to send, if any.</summary>
    private async Task<AlertMessage?> PersistAsync(MonitorEntity snapshot, CheckOutcome outcome, CheckResult result, DateTime checkedAt)
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
        AlertMessage? alert = null;
        var unchangedLifecycle = monitor.Status == snapshot.Status && monitor.IsEnabled == snapshot.IsEnabled;
        if (unchangedLifecycle && MonitorStateMachine.IsActivelyMonitored(snapshot) && MonitorStateMachine.IsActivelyMonitored(monitor))
        {
            var transition = MonitorStateMachine.Apply(monitor, outcome, checkedAt);
            alert = await ApplyTransitionAsync(db, monitor, transition, checkedAt, ct);
            // Status changed (and possibly an incident opened/closed): make concurrent user writers retry.
            if (transition != StatusTransition.None) monitor.Version++;
        }
        else
        {
            MonitorStateMachine.RecordProbe(monitor, outcome, checkedAt);
            logger.LogDebug("Check of inactive monitor {MonitorId} recorded without state change: {IsUp}", monitor.Id, outcome.IsUp);
        }

        await db.SaveChangesAsync(ct);
        return alert;
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
            logger.LogError(ex, "Checker for {MonitorType} threw for monitor {MonitorId}", monitor.Type, monitor.Id);
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
