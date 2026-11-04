using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Monitoring.Escalation;

/// <summary>
/// One pass of the escalation job (FEAT-5). For every open, unacknowledged incident of a DOWN monitor whose customer has
/// an enabled <see cref="EscalationPolicy"/>: when steps beyond <see cref="Incident.LastEscalationStep"/> are due
/// (incident start + <see cref="EscalationStep.DelayMinutes"/> &lt;= now), one <see cref="AlertKind.Escalation"/> alert
/// goes to the channels of all newly due steps (normally one step; several after downtime of the job) through
/// <see cref="IAlertQueue"/>.
/// <list type="bullet">
/// <item>Idempotent across restarts and concurrent runs: the step is claimed with a conditional UPDATE of
/// LastEscalationStep (only if it is unchanged and the incident is still open and unacknowledged) before the alert is
/// queued, so a step is never sent twice. An alert lost in the in-memory queue by a crash is not repeated (the queue's
/// known limitation).</item>
/// <item>Acknowledged or resolved incidents never escalate; the dispatcher re-checks right before delivery.</item>
/// <item>While the monitor is in a maintenance window, escalation pauses; due steps follow once the window ends.</item>
/// <item>No policy (or a disabled one): nothing happens, the DOWN alert remains the only notification.</item>
/// </list>
/// </summary>
public sealed class EscalationRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IAlertQueue alerts,
    TimeProvider time,
    MonitoringMetrics metrics,
    ILogger<EscalationRunner> logger)
{
    public const int MaxSteps = 5;
    public const int MaxDelayMinutes = 24 * 60;

    /// <summary>Runs one pass; returns the number of escalation alerts queued.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var policies = await db.EscalationPolicies.AsNoTracking()
            .Where(p => p.IsEnabled && p.Customer.IsActive && p.Steps.Any())
            .Select(p => new
            {
                p.CustomerId,
                Steps = p.Steps.OrderBy(s => s.DelayMinutes).ThenBy(s => s.Id)
                    .Select(s => new { s.DelayMinutes, ChannelIds = s.Channels.Select(c => c.Id).ToList() }).ToList()
            })
            .ToListAsync(ct);
        if (policies.Count == 0) return 0;
        var byCustomer = policies.ToDictionary(p => p.CustomerId);
        var customerIds = byCustomer.Keys.ToList();

        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAtUtc == null && i.AcknowledgedAtUtc == null && customerIds.Contains(i.Monitor.CustomerId)
                        && i.Monitor.Status == MonitorStatus.Down && i.Monitor.IsEnabled)
            .Select(i => new { i.Id, i.StartedAtUtc, i.LastEscalationStep, i.Monitor })
            .ToListAsync(ct);

        var queued = 0;
        foreach (var incident in incidents)
        {
            var steps = byCustomer[incident.Monitor.CustomerId].Steps;
            var due = steps.Count(s => incident.StartedAtUtc.AddMinutes(s.DelayMinutes) <= now);
            if (due <= incident.LastEscalationStep) continue;
            if (await MaintenanceQueries.IsInMaintenanceAsync(db, incident.Monitor.CustomerId, incident.Monitor.Id, now, ct)) continue;

            var previous = incident.LastEscalationStep;
            var claimed = await db.Incidents
                .Where(i => i.Id == incident.Id && i.LastEscalationStep == previous && i.ResolvedAtUtc == null && i.AcknowledgedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastEscalationStep, due), ct);
            if (claimed != 1) continue; // acknowledged, resolved or escalated concurrently

            var channelIds = steps.Skip(previous).Take(due - previous).SelectMany(s => s.ChannelIds).Distinct().ToList();
            var message = AlertMessage.ForEscalation(incident.Monitor, incident.Id, incident.StartedAtUtc, due, channelIds, now);
            logger.LogWarning("Incident {IncidentId} of monitor {MonitorId} is unacknowledged after {Minutes} min: escalation level {Level}",
                incident.Id, incident.Monitor.Id, (int)(now - incident.StartedAtUtc).TotalMinutes, due);
            metrics.RecordEscalation();
            if (alerts.TryEnqueue(message)) queued++;
        }
        return queued;
    }
}

/// <summary>Runs <see cref="EscalationRunner"/> every <see cref="EscalationOptions.CheckIntervalSeconds"/>. Off when Monitoring:Enabled=false.</summary>
public sealed class EscalationService(
    IServiceScopeFactory scopes,
    IOptions<MonitoringOptions> monitoring,
    IOptions<EscalationOptions> options,
    TimeProvider time,
    ILogger<EscalationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!monitoring.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.CheckIntervalSeconds, 5, 600)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<EscalationRunner>().RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Escalation run failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
