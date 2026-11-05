using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Cluster;

namespace TriggerCheck.Ai.Features.Correlation;

/// <summary>
/// One AI-4 scan (cluster leader only). A3 first, whatever the mode: undo automatic groups whose primary closed or
/// whose customer is no longer in <see cref="CorrelationMode.Automatic"/>, queue the held-back DOWN of every folded
/// member the fold no longer covers (durable retry), send the debounced "also affected" follow-ups. Then expire
/// undecided proposals, create proposals (A1) for customers in <see cref="CorrelationMode.Suggest"/>, and queue the AI
/// explanation of new or extended groups (the LLM only explains and judges ambiguous candidates; never on the alert
/// path). <paramref name="renew"/> is asked before each step with side effects; false stops the scan (fencing).
/// </summary>
public sealed class CorrelationScanner(
    AppDbContext db,
    IncidentCorrelationService groups,
    IAiJobQueue queue,
    IAiClient client,
    IOptions<AiOptions> aiOptions,
    IOptions<AiCorrelationOptions> options,
    CorrelationAutonomy autonomy,
    TimeProvider time,
    ILogger<CorrelationScanner> logger)
{
    public sealed record Result(int Proposed, int Settled, int JobsQueued, int Notified = 0, int FollowUps = 0);

    public async Task<Result> ScanAsync(CancellationToken ct = default, Func<CancellationToken, Task<bool>>? renew = null)
    {
        async Task<bool> Hold() => renew is null || await renew(ct);

        if (!await Hold()) return new(0, 0, 0);
        var settled = await groups.SettleAutomaticAsync(ct);
        if (!await Hold()) return new(0, settled, 0);
        var notified = await groups.NotifyPendingAsync(null, null, ct);
        if (!await Hold()) return new(0, settled, 0, notified);
        var followUps = await groups.SendFollowUpsAsync(ct);
        if (!await Hold()) return new(0, settled, 0, notified, followUps);
        await groups.ExpireAsync(ct);
        await groups.RefreshRuntimeStateAsync(ct);
        if (!aiOptions.Value.Enabled || !autonomy.Enabled) return new(0, settled, 0, notified, followUps);

        var since = time.GetUtcNow().UtcDateTime.AddMinutes(-Math.Max(1, options.Value.LookbackMinutes));
        var customerIds = await db.Incidents.AsNoTracking()
            .Where(i => i.StartedAtUtc >= since && !db.IncidentGroupMembers.Any(m => m.IncidentId == i.Id))
            .Select(i => i.Monitor.CustomerId).Distinct().OrderBy(id => id).ToListAsync(ct);
        var unexplained = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.CreatedAtUtc >= since && g.ExplanationQueuedAtUtc == null
                        && (g.Status == IncidentGroupStatus.Proposed || g.Status == IncidentGroupStatus.Auto))
            .Select(g => g.CustomerId).Distinct().ToListAsync(ct);

        var proposed = 0;
        var queued = 0;
        foreach (var cid in customerIds.Union(unexplained).Distinct().Order())
        {
            var customer = await groups.CustomerAsync(cid, ct);
            if (customer is null) continue;
            var mode = await groups.ModeAsync(customer, ct);
            if (mode == CorrelationMode.Off) continue;
            if (!await Hold()) break;
            // Automatic mode groups on the alert path; proposals are only made in Suggest mode.
            if (mode == CorrelationMode.Suggest && customerIds.Contains(cid)) proposed += (await groups.ProposeAsync(cid, ct)).Count;
            if (client.IsAvailable) queued += await QueueExplanationsAsync(customer, since, ct);
        }
        if (proposed > 0 || queued > 0 || settled > 0 || notified > 0 || followUps > 0)
            logger.LogDebug("Incident correlation: {Proposed} proposal(s), {Queued} explanation job(s), {Settled} automatic group(s) undone, {Notified} held-back alert(s), {FollowUps} follow-up(s)",
                proposed, queued, settled, notified, followUps);
        return new(proposed, settled, queued, notified, followUps);
    }

    /// <summary>
    /// Queues one explanation per group and membership (marked <see cref="IncidentGroup.ExplanationQueuedAtUtc"/> once
    /// queued), so a group the model cannot explain (the provider builds nothing) is not queued again every scan. A full
    /// queue leaves it unmarked for the next scan.
    /// </summary>
    private async Task<int> QueueExplanationsAsync(Customer customer, DateTime since, CancellationToken ct)
    {
        var ids = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.CustomerId == customer.Id && g.CreatedAtUtc >= since && g.ExplanationQueuedAtUtc == null
                        && (g.Status == IncidentGroupStatus.Proposed || g.Status == IncidentGroupStatus.Auto))
            .OrderBy(g => g.Id).Select(g => g.Id).ToListAsync(ct);
        var language = AiJob.NormalizeLanguage(customer.AiLanguage);
        var queued = 0;
        foreach (var id in ids)
        {
            if (!queue.TryEnqueue(new AiJob(AiFeatures.CorrelationGrouping, customer.Id, IncidentCorrelationService.SubjectType, id.ToString(CultureInfo.InvariantCulture), language)))
                break;
            var now = time.GetUtcNow().UtcDateTime;
            await db.IncidentGroups.Where(g => g.Id == id && g.ExplanationQueuedAtUtc == null)
                .ExecuteUpdateAsync(u => u.SetProperty(g => g.ExplanationQueuedAtUtc, now), ct);
            queued++;
        }
        return queued;
    }
}

/// <summary>
/// Runs <see cref="CorrelationScanner"/> every <see cref="AiCorrelationOptions.PollSeconds"/>. Every instance refreshes
/// its <see cref="CorrelationRuntimeState"/>; only the holder of the <see cref="LeaseName"/> cluster lease (OPS-3)
/// scans, renewing the lease before each step with side effects.
/// With <c>Ai:Enabled</c> off (a restart setting) nothing new can be grouped, so the watcher only runs while automatic
/// groups or held-back DOWNs from before are left to undo / send, and then stops.
/// </summary>
public sealed class CorrelationWatcher(IServiceScopeFactory scopes, IOptions<AiCorrelationOptions> options, IOptions<AiOptions> aiOptions,
    CorrelationRuntimeState runtime, TimeProvider time, ILogger<CorrelationWatcher> logger)
    : BackgroundService
{
    public const string LeaseName = "ai-correlation";

    /// <summary>AI off and nothing left from automatic grouping: the watcher has nothing to do.</summary>
    private async Task<bool> IdleAsync(CancellationToken ct)
    {
        if (aiOptions.Value.Enabled) return false;
        using var scope = scopes.CreateScope();
        if (await scope.ServiceProvider.GetRequiredService<IncidentCorrelationService>().HasAutomaticWorkAsync(ct)) return false;
        runtime.AutoGroupsMayExist = false;
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await IdleAsync(stoppingToken)) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Incident correlation start-up check failed; watching anyway");
        }
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds));
        var lease = TimeSpan.FromSeconds(Math.Max(60, 3 * poll.TotalSeconds));
        using var timer = new PeriodicTimer(poll, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    await sp.GetRequiredService<IncidentCorrelationService>().RefreshRuntimeStateAsync(stoppingToken);
                    var leases = sp.GetRequiredService<ClusterLeases>();
                    if (!await leases.TryAcquireAsync(LeaseName, lease, stoppingToken)) continue;
                    await sp.GetRequiredService<CorrelationScanner>().ScanAsync(stoppingToken, ct => leases.RenewAsync(LeaseName, lease, ct));
                    if (await IdleAsync(stoppingToken)) return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Incident correlation scan failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await ClusterLeases.ReleaseOnStopAsync(scopes, LeaseName);
        }
    }
}

/// <summary>The AI layer's <see cref="IAlertGrouping"/> (AI-4, A3): delegates to <see cref="IncidentCorrelationService"/>.</summary>
public sealed class CorrelationAlertGrouping(IncidentCorrelationService groups) : IAlertGrouping
{
    public Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct) => groups.TryFoldAsync(message, ct);

    public Task AfterDeliveredAsync(AlertMessage message, CancellationToken ct) => groups.AfterDeliveredAsync(message, ct);
}
