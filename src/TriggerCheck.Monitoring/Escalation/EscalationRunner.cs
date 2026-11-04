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
/// an enabled <see cref="EscalationPolicy"/>: when steps with a delay beyond <see cref="Incident.LastEscalationDelayMinutes"/>
/// are due (max(incident start, <see cref="EscalationPolicy.EnabledAtUtc"/>) + <see cref="EscalationStep.DelayMinutes"/>
/// &lt;= now), one <see cref="AlertKind.Escalation"/> alert goes to the channels of all newly due steps (normally one
/// step; several after downtime of the job) through <see cref="IAlertQueue"/>. Progress is kept as the largest delay
/// sent, so policy edits during an open incident neither repeat nor skip steps.
/// <list type="bullet">
/// <item>Idempotent across restarts and concurrent runs: the step is claimed with a conditional UPDATE of
/// LastEscalationDelayMinutes (only if it is unchanged and the incident is still open and unacknowledged) before the
/// alert is queued, so a step is never sent twice. If the queue refuses the alert the claim is rolled back and the next
/// pass retries. An alert lost in the in-memory queue by a crash is not repeated (the queue's known limitation).</item>
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
                p.EnabledAtUtc,
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
            .Select(i => new { i.Id, i.StartedAtUtc, i.LastEscalationStep, i.LastEscalationDelayMinutes, i.Monitor })
            .ToListAsync(ct);

        var queued = 0;
        foreach (var incident in incidents)
        {
            var policy = byCustomer[incident.Monitor.CustomerId];
            // Delays count from the incident start, or from when the policy was (re-)enabled if that is later.
            var from = incident.StartedAtUtc > policy.EnabledAtUtc ? incident.StartedAtUtc : policy.EnabledAtUtc;
            var previousDelay = incident.LastEscalationDelayMinutes;
            var due = policy.Steps.Select((s, i) => (Step: s, Level: i + 1))
                .Where(x => x.Step.DelayMinutes > previousDelay && from.AddMinutes(x.Step.DelayMinutes) <= now)
                .ToList();
            if (due.Count == 0) continue;
            if (await MaintenanceQueries.IsInMaintenanceAsync(db, incident.Monitor.CustomerId, incident.Monitor.Id, now, ct)) continue;

            var level = due[^1].Level;
            var delay = due[^1].Step.DelayMinutes;
            var previousLevel = incident.LastEscalationStep;
            var claimed = await db.Incidents
                .Where(i => i.Id == incident.Id && i.LastEscalationDelayMinutes == previousDelay && i.ResolvedAtUtc == null && i.AcknowledgedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastEscalationDelayMinutes, delay).SetProperty(i => i.LastEscalationStep, level), ct);
            if (claimed != 1) continue; // acknowledged, resolved or escalated concurrently

            var channelIds = due.SelectMany(x => x.Step.ChannelIds).Distinct().ToList();
            var message = AlertMessage.ForEscalation(incident.Monitor, incident.Id, incident.StartedAtUtc, level, channelIds, now);
            if (!alerts.TryEnqueue(message))
            {
                // Queue full: give the step back so the next pass tries again.
                await db.Incidents
                    .Where(i => i.Id == incident.Id && i.LastEscalationDelayMinutes == delay)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.LastEscalationDelayMinutes, previousDelay).SetProperty(i => i.LastEscalationStep, previousLevel), CancellationToken.None);
                continue;
            }
            logger.LogWarning("Incident {IncidentId} of monitor {MonitorId} is unacknowledged after {Minutes} min: escalation level {Level}",
                incident.Id, incident.Monitor.Id, (int)(now - incident.StartedAtUtc).TotalMinutes, level);
            metrics.RecordEscalation();
            queued++;
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
