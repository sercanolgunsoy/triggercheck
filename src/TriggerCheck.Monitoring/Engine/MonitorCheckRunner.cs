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
/// <item>deleted meanwhile, or its probe settings (target, type, timeout, ...) were edited: the stale result is
/// discarded;</item>
/// <item>paused / disabled / suspended meanwhile: only the probe is recorded - never a transition, incident or
/// alert;</item>
/// <item>a user change lands between the re-read and the save: the save fails on the token and is retried
/// against the fresh row.</item>
/// </list>
/// Alert delivery happens after the state is saved and its failures are only logged.
/// </summary>
public sealed class MonitorCheckRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IMonitorChecker> checkers,
    IAlertDispatcher alerts,
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

        return result;
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
            return null;
        }
        if (monitor.Version != snapshot.Version && !SameProbeSettings(snapshot, monitor))
        {
            logger.LogDebug("Monitor {MonitorId} was edited during its check; stale result discarded", snapshot.Id);
            return null;
        }

        db.CheckResults.Add(result);
        AlertMessage? alert = null;
        if (MonitorStateMachine.IsActivelyMonitored(snapshot) && MonitorStateMachine.IsActivelyMonitored(monitor))
        {
            var transition = MonitorStateMachine.Apply(monitor, outcome, checkedAt);
            alert = await ApplyTransitionAsync(db, monitor, transition, checkedAt, ct);
        }
        else
        {
            MonitorStateMachine.RecordProbe(monitor, outcome, checkedAt);
            logger.LogDebug("Check of inactive monitor {MonitorId} recorded without state change: {IsUp}", monitor.Id, outcome.IsUp);
        }

        await db.SaveChangesAsync(ct);
        return alert;
    }

    private static bool SameProbeSettings(MonitorEntity a, MonitorEntity b) =>
        a.Type == b.Type && a.Target == b.Target && a.Port == b.Port && a.Keyword == b.Keyword
        && a.ExpectedStatusCodes == b.ExpectedStatusCodes && a.HttpMethod == b.HttpMethod && a.TimeoutSeconds == b.TimeoutSeconds;

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
            return CheckOutcome.Down("Internal checker error: " + ex.Message);
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
