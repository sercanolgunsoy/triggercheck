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
/// fields, NextCheckAtUtc = check time + interval, status transitions, incident open/close, DOWN/UP alerts.</item>
/// <item>Paused / disabled / customer suspended ("check now" from the UI): the probe runs and a CheckResult plus
/// LastCheckedAtUtc / LastResponseTimeMs / LastError are recorded, but Status, ConsecutiveFailures, NextCheckAtUtc,
/// incidents and alerts are left untouched. A paused monitor therefore stays Paused.</item>
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
    public async Task<CheckResult> RunAsync(int monitorId, CancellationToken ct = default)
    {
        CheckResult result;
        AlertMessage? alert = null;

        using (await gate.AcquireAsync(monitorId, ct))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var monitor = await db.Monitors.Include(m => m.Customer).SingleOrDefaultAsync(m => m.Id == monitorId, ct)
                ?? throw new KeyNotFoundException($"Monitor {monitorId} not found.");

            var checkedAt = time.GetUtcNow().UtcDateTime;
            var outcome = await ProbeAsync(monitor, ct);

            result = new CheckResult
            {
                MonitorId = monitor.Id,
                CheckedAtUtc = checkedAt,
                IsUp = outcome.IsUp,
                StatusCode = outcome.StatusCode,
                ResponseTimeMs = outcome.ResponseTimeMs,
                Error = outcome.Error
            };
            db.CheckResults.Add(result);

            if (!MonitorStateMachine.IsActivelyMonitored(monitor))
            {
                MonitorStateMachine.RecordProbe(monitor, outcome, checkedAt);
                logger.LogDebug("Manual check of inactive monitor {MonitorId}: {IsUp}", monitor.Id, outcome.IsUp);
            }
            else
            {
                var transition = MonitorStateMachine.Apply(monitor, outcome, checkedAt);
                alert = await ApplyTransitionAsync(db, monitor, transition, checkedAt, ct);
            }

            // State must be persisted even if shutdown was requested after the probe completed.
            await db.SaveChangesAsync(CancellationToken.None);
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
