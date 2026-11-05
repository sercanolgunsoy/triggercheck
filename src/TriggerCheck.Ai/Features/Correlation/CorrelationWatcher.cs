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

namespace TriggerCheck.Ai.Features.Correlation;

/// <summary>
/// One AI-4 scan: undoes automatic groups whose primary closed without an UP alert, expires undecided proposals, creates
/// proposals (A1) for customers in <see cref="CorrelationMode.Suggest"/>, and queues the AI explanation of new or
/// extended groups (the LLM only explains and judges the ambiguous candidates; it never runs on the alert path).
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
    public sealed record Result(int Proposed, int Settled, int JobsQueued);

    public async Task<Result> ScanAsync(CancellationToken ct = default)
    {
        var settled = await groups.SettleAutomaticAsync(ct);
        await groups.ExpireAsync(ct);
        if (!aiOptions.Value.Enabled || !autonomy.Enabled) return new(0, settled, 0);

        var since = time.GetUtcNow().UtcDateTime.AddMinutes(-Math.Max(1, options.Value.LookbackMinutes));
        var customerIds = await db.Incidents.AsNoTracking()
            .Where(i => i.StartedAtUtc >= since && !db.IncidentGroupMembers.Any(m => m.IncidentId == i.Id))
            .Select(i => i.Monitor.CustomerId).Distinct().OrderBy(id => id).ToListAsync(ct);
        var recentGroups = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.CreatedAtUtc >= since && (g.Status == IncidentGroupStatus.Proposed || g.Status == IncidentGroupStatus.Auto))
            .Select(g => new { g.Id, g.CustomerId }).ToListAsync(ct);

        var proposed = 0;
        var toExplain = new Dictionary<int, HashSet<int>>(); // customer → group ids
        foreach (var cid in customerIds.Union(recentGroups.Select(g => g.CustomerId)).Distinct())
        {
            var customer = await groups.CustomerAsync(cid, ct);
            if (customer is null) continue;
            var mode = await groups.ModeAsync(customer, ct);
            if (mode == CorrelationMode.Off) continue;
            var set = toExplain[cid] = [];
            // Automatic mode groups on the alert path; proposals are only made in Suggest mode.
            if (mode == CorrelationMode.Suggest && customerIds.Contains(cid))
            {
                var ids = await groups.ProposeAsync(cid, ct);
                proposed += ids.Count;
                set.UnionWith(ids);
            }
            // Recent groups without any AI decision yet (e.g. the queue was full, or made on the alert path).
            var recent = recentGroups.Where(g => g.CustomerId == cid).Select(g => g.Id.ToString(CultureInfo.InvariantCulture)).ToList();
            if (recent.Count > 0)
            {
                var decided = await db.AiDecisions.AsNoTracking()
                    .Where(d => d.CustomerId == cid && d.Feature == AiFeatures.CorrelationGrouping && d.SubjectType == IncidentCorrelationService.SubjectType
                                && recent.Contains(d.SubjectId))
                    .Select(d => d.SubjectId).Distinct().ToListAsync(ct);
                set.UnionWith(recent.Except(decided).Select(s => int.Parse(s, CultureInfo.InvariantCulture)));
            }
        }

        var queued = 0;
        if (client.IsAvailable)
        {
            foreach (var (cid, ids) in toExplain)
            {
                var language = AiJob.NormalizeLanguage((await groups.CustomerAsync(cid, ct))?.AiLanguage);
                foreach (var id in ids.Order())
                    if (queue.TryEnqueue(new AiJob(AiFeatures.CorrelationGrouping, cid, IncidentCorrelationService.SubjectType, id.ToString(CultureInfo.InvariantCulture), language)))
                        queued++;
            }
        }
        if (proposed > 0 || queued > 0 || settled > 0)
            logger.LogDebug("Incident correlation: {Proposed} proposal(s), {Queued} explanation job(s), {Settled} automatic group(s) undone", proposed, queued, settled);
        return new(proposed, settled, queued);
    }
}

/// <summary>Runs <see cref="CorrelationScanner"/> every <see cref="AiCorrelationOptions.PollSeconds"/>.</summary>
public sealed class CorrelationWatcher(IServiceScopeFactory scopes, IOptions<AiCorrelationOptions> options, TimeProvider time, ILogger<CorrelationWatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<CorrelationScanner>().ScanAsync(stoppingToken);
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
    }
}

/// <summary>The AI layer's <see cref="IAlertGrouping"/> (AI-4, A3): delegates to <see cref="IncidentCorrelationService.TryFoldAsync"/>.</summary>
public sealed class CorrelationAlertGrouping(IncidentCorrelationService groups) : IAlertGrouping
{
    public Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct) => groups.TryFoldAsync(message, ct);
}
