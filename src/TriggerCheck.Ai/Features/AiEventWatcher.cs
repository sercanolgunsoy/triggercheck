using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Features;

/// <summary>
/// Finds the events AI-2 and AI-3 react to and queues one job each:
/// <list type="bullet">
/// <item>incident opened (new incident id) and incident resolved (ResolvedAtUtc within the look-back) → AI-2;</item>
/// <item>anomaly signal opened (new signal id) → AI-3.</item>
/// </list>
/// Why a watcher instead of a call from the check runner: the monitoring engine stays free of any AI dependency
/// (TriggerCheck.Monitoring never references this project), the DOWN / UP alert path is not touched at all, so it can
/// never be delayed (decision K4), and incidents opened by any writer (runner, heartbeats, pause/resume) are covered.
/// Cost: one small indexed query per kind every <see cref="AiEventOptions.PollSeconds"/>, so a summary is ready about
/// that much (plus the model call) after the alert.
/// Customers that opted out or whose plan lacks the feature are not queued (the UI shows that state from the customer
/// itself); a used-up budget is queued and recorded as SkippedBudget by the interpreter. Only id watermarks and a short
/// in-memory list of queued resolve events are kept: after a restart, events of the last
/// <see cref="AiEventOptions.LookbackMinutes"/> without an AI decision are picked up again.
/// </summary>
public sealed class AiEventScanner(AppDbContext db, IAiJobQueue queue, IOptions<AiOptions> options, TimeProvider time, ILogger<AiEventScanner> logger)
{
    public const int BatchSize = 500;

    /// <summary>Watermarks, kept by the hosted service between scans.</summary>
    public sealed class State
    {
        public bool Initialized { get; set; }
        public int LastIncidentId { get; set; }
        public long LastSignalId { get; set; }
        /// <summary>Resolve events already queued (incident id → resolved at), pruned after the look-back.</summary>
        public Dictionary<int, DateTime> QueuedResolves { get; } = new();
    }

    private sealed record Eligible(int CustomerId, string Language, IReadOnlyList<string> Features);

    /// <returns>The number of jobs queued.</returns>
    public async Task<int> ScanAsync(State state, CancellationToken ct = default)
    {
        var o = options.Value.Events;
        var now = time.GetUtcNow().UtcDateTime;
        var since = now.AddMinutes(-Math.Max(1, o.LookbackMinutes));
        foreach (var old in state.QueuedResolves.Where(kv => kv.Value < since).Select(kv => kv.Key).ToList())
            state.QueuedResolves.Remove(old);

        // ---- candidates ----
        List<(int Id, int CustomerId)> opened;
        List<(long Id, int CustomerId)> signals;
        if (!state.Initialized)
        {
            // First scan after start: recent events without a decision (the process may have stopped before them).
            var maxIncident = await db.Incidents.MaxAsync(i => (int?)i.Id, ct) ?? 0;
            var maxSignal = await db.AnomalySignals.MaxAsync(s => (long?)s.Id, ct) ?? 0;
            opened = (await db.Incidents.AsNoTracking()
                    .Where(i => i.Id <= maxIncident && i.StartedAtUtc >= since && i.ResolvedAtUtc == null)
                    .Select(i => new { i.Id, i.Monitor.CustomerId }).ToListAsync(ct))
                .Select(x => (x.Id, x.CustomerId)).ToList();
            opened = await WithoutDecisionAsync(opened, AiFeatures.IncidentInterpretation, AiSubjects.Incident, x => x.Id.ToString(CultureInfo.InvariantCulture), ct);
            signals = (await db.AnomalySignals.AsNoTracking()
                    .Where(s => s.Id <= maxSignal && s.EndedAtUtc == null && s.UpdatedAtUtc >= since)
                    .Select(s => new { s.Id, s.CustomerId }).ToListAsync(ct))
                .Select(x => (x.Id, x.CustomerId)).ToList();
            signals = await WithoutDecisionAsync(signals, AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal, x => x.Id.ToString(CultureInfo.InvariantCulture), ct);
            state.LastIncidentId = maxIncident;
            state.LastSignalId = maxSignal;
            state.Initialized = true;
        }
        else
        {
            var last = state.LastIncidentId;
            opened = (await db.Incidents.AsNoTracking().Where(i => i.Id > last).OrderBy(i => i.Id).Take(BatchSize)
                    .Select(i => new { i.Id, i.Monitor.CustomerId }).ToListAsync(ct))
                .Select(x => (x.Id, x.CustomerId)).ToList();
            if (opened.Count > 0) state.LastIncidentId = opened[^1].Id;
            var lastSignal = state.LastSignalId;
            signals = (await db.AnomalySignals.AsNoTracking().Where(s => s.Id > lastSignal).OrderBy(s => s.Id).Take(BatchSize)
                    .Select(s => new { s.Id, s.CustomerId }).ToListAsync(ct))
                .Select(x => (x.Id, x.CustomerId)).ToList();
            if (signals.Count > 0) state.LastSignalId = signals[^1].Id;
        }

        // Resolved recently, not queued yet, and no decision made after the resolution.
        var resolvedRows = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAtUtc != null && i.ResolvedAtUtc >= since)
            .Select(i => new { i.Id, i.Monitor.CustomerId, ResolvedAtUtc = i.ResolvedAtUtc!.Value })
            .ToListAsync(ct);
        resolvedRows = resolvedRows.Where(r => !state.QueuedResolves.ContainsKey(r.Id)).ToList();
        var resolvedIds = resolvedRows.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var decided = resolvedIds.Count == 0 ? [] : await db.AiDecisions.AsNoTracking()
            .Where(d => d.Feature == AiFeatures.IncidentInterpretation && d.SubjectType == AiSubjects.Incident && resolvedIds.Contains(d.SubjectId))
            .Select(d => new { d.SubjectId, d.CreatedAtUtc })
            .ToListAsync(ct);
        var resolved = resolvedRows
            .Where(r => !decided.Any(d => d.SubjectId == r.Id.ToString(CultureInfo.InvariantCulture) && d.CreatedAtUtc >= r.ResolvedAtUtc))
            .ToList();
        // An incident opened and resolved since the last scan needs only one job (it reads the resolved state).
        opened = opened.Where(x => !resolved.Any(r => r.Id == x.Id)).ToList();

        if (opened.Count + signals.Count + resolved.Count == 0) return 0;

        // ---- eligibility (opt-in, plan, active) ----
        var customerIds = opened.Select(x => x.CustomerId).Concat(signals.Select(x => x.CustomerId)).Concat(resolved.Select(x => x.CustomerId))
            .Distinct().ToList();
        var customers = (await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id) && c.IsActive && c.AiEnabled)
                .Select(c => new { c.Id, c.AiLanguage, c.Plan.AiFeatures, c.Plan.AiMonthlyCallLimit })
                .ToListAsync(ct))
            .Where(c => c.AiMonthlyCallLimit > 0)
            .ToDictionary(c => c.Id, c => new Eligible(c.Id, AiJob.NormalizeLanguage(c.AiLanguage), AiFeatures.Parse(c.AiFeatures)));

        var queued = 0;
        void Enqueue(int customerId, string feature, string subjectType, string subjectId)
        {
            if (!customers.TryGetValue(customerId, out var c) || !c.Features.Contains(feature)) return;
            if (queue.TryEnqueue(new AiJob(feature, customerId, subjectType, subjectId, c.Language))) queued++;
        }

        foreach (var (id, customerId) in opened)
            Enqueue(customerId, AiFeatures.IncidentInterpretation, AiSubjects.Incident, id.ToString(CultureInfo.InvariantCulture));
        foreach (var r in resolved)
        {
            Enqueue(r.CustomerId, AiFeatures.IncidentInterpretation, AiSubjects.Incident, r.Id.ToString(CultureInfo.InvariantCulture));
            // Marked even when the queue refused it (full, or the same subject still waiting): no retry every tick.
            state.QueuedResolves[r.Id] = r.ResolvedAtUtc;
        }
        foreach (var (id, customerId) in signals)
            Enqueue(customerId, AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal, id.ToString(CultureInfo.InvariantCulture));

        if (queued > 0) logger.LogDebug("Queued {Count} AI job(s) for incidents / slowdowns", queued);
        return queued;
    }

    private async Task<List<(TId Id, int CustomerId)>> WithoutDecisionAsync<TId>(List<(TId Id, int CustomerId)> items, string feature, string subjectType,
        Func<(TId Id, int CustomerId), string> subjectId, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        var ids = items.Select(subjectId).ToList();
        var done = (await db.AiDecisions.AsNoTracking()
                .Where(d => d.Feature == feature && d.SubjectType == subjectType && ids.Contains(d.SubjectId))
                .Select(d => d.SubjectId).Distinct().ToListAsync(ct))
            .ToHashSet();
        return items.Where(i => !done.Contains(subjectId(i))).ToList();
    }
}

/// <summary>
/// Runs <see cref="AiEventScanner"/> every <see cref="AiEventOptions.PollSeconds"/>. Does nothing when AI is off or no
/// key is configured (Ai:Enabled needs a restart, so this is decided once at start).
/// </summary>
public sealed class AiEventWatcher(IServiceScopeFactory scopes, IAiClient client, IOptions<AiOptions> options, TimeProvider time, ILogger<AiEventWatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable) return;

        var state = new AiEventScanner.State();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.Events.PollSeconds)), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AiEventScanner>().ScanAsync(state, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AI event scan failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
