using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Features;

/// <summary>
/// Finds the events AI-2 and AI-3 react to and queues one job each:
/// <list type="bullet">
/// <item>incident opened (new incident id) and incident resolved (ResolvedAtUtc after a watermark) → AI-2;</item>
/// <item>anomaly signal opened (new signal id) → AI-3.</item>
/// </list>
/// Why a watcher instead of a call from the check runner: the monitoring engine stays free of any AI dependency
/// (TriggerCheck.Monitoring never references this project), the DOWN / UP alert path is not touched at all, so it can
/// never be delayed (decision K4), and incidents opened by any writer (runner, heartbeats, pause/resume) are covered.
/// <para>
/// Damping and fairness (cost is bounded by events, not by monitors):
/// <list type="bullet">
/// <item>per monitor: a new incident is not interpreted when the same monitor had one interpreted within
/// <see cref="AiEventOptions.MonitorCooldownMinutes"/> (flapping); its resolution is skipped too;</item>
/// <item>per host: of incidents of one customer on the same host within <see cref="AiEventOptions.HostWindowMinutes"/>,
/// only the first is interpreted (its context lists the other monitors on the host; their pages show it);</item>
/// <item>at most <see cref="AiEventOptions.MaxJobsPerCustomerPerScan"/> jobs per customer per scan, taken round-robin
/// across customers, so one noisy tenant cannot starve the others;</item>
/// <item>events over the cap or refused by a full queue are kept and retried on the next scan (up to the look-back).</item>
/// </list>
/// Customers that opted out or whose plan lacks the feature are not queued (the UI shows that state from the customer
/// itself); a used-up budget is queued and recorded as SkippedBudget by the interpreter. After a restart, events of the
/// last <see cref="AiEventOptions.LookbackMinutes"/> without an AI decision are picked up again.
/// </para>
/// </summary>
public sealed class AiEventScanner(AppDbContext db, IAiJobQueue queue, IOptions<AiOptions> options, TimeProvider time, ILogger<AiEventScanner> logger)
{
    public const int BatchSize = 500, MaxDeferred = 5000;
    /// <summary>ResolvedAtUtc is the probe start, written when the probe ends: re-read this much before the watermark.</summary>
    public static readonly TimeSpan ResolvedOverlap = TimeSpan.FromMinutes(2);

    public enum EventKind { Opened, Resolved, Slowdown }

    public sealed record Event(EventKind Kind, long SubjectId, int CustomerId, int MonitorId, DateTime AtUtc, DateTime FirstSeenUtc)
    {
        public (EventKind, long) Key => (Kind, SubjectId);
        public string Feature => Kind == EventKind.Slowdown ? AiFeatures.SlowdownExplanation : AiFeatures.IncidentInterpretation;
        public string SubjectType => Kind == EventKind.Slowdown ? AiSubjects.AnomalySignal : AiSubjects.Incident;
        public string Subject => SubjectId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Watermarks and short-lived memory, kept by the hosted service between scans.</summary>
    public sealed class State
    {
        public bool Initialized { get; set; }
        public int LastIncidentId { get; set; }
        public long LastSignalId { get; set; }
        /// <summary>Keyset watermark (ResolvedAtUtc, Id) of resolved incidents older than <see cref="ResolvedOverlap"/>.</summary>
        public (DateTime At, int Id)? ResolvedWatermark { get; set; }
        /// <summary>Resolve events already handled (queued or damped): incident id → resolved at.</summary>
        public Dictionary<int, DateTime> HandledResolves { get; } = new();
        /// <summary>Events not queued yet (over the per-customer cap or refused by the queue); retried next scan.</summary>
        public Dictionary<(EventKind, long), Event> Deferred { get; } = new();
        /// <summary>When an AI-2 job was last queued per monitor / per (customer, host).</summary>
        public Dictionary<int, DateTime> MonitorQueuedAt { get; } = new();
        /// <summary>Normalised cause of the incident last queued per monitor (a different cause bypasses the cool-down).</summary>
        public Dictionary<int, string> MonitorQueuedCause { get; } = new();
        /// <summary>When a changed cause last bypassed the cool-down per monitor (at most once per cool-down).</summary>
        public Dictionary<int, DateTime> CauseBypassAt { get; } = new();
        /// <summary>Same-host incidents waiting behind the incident chosen for their host (winner id → siblings).</summary>
        public Dictionary<long, List<Event>> HostSiblings { get; } = new();
        public Dictionary<(int, string), DateTime> HostQueuedAt { get; } = new();
        /// <summary>Incidents with an AI-2 job queued (its resolution is interpreted even before the first job finished).</summary>
        public Dictionary<long, DateTime> QueuedIncidents { get; } = new();
    }

    private sealed record Eligible(string Language, IReadOnlyList<string> Features);

    /// <returns>The number of jobs queued.</returns>
    public async Task<int> ScanAsync(State state, CancellationToken ct = default)
    {
        var o = options.Value.Events;
        var now = time.GetUtcNow().UtcDateTime;
        var since = now.AddMinutes(-Math.Max(1, o.LookbackMinutes));
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, o.MonitorCooldownMinutes));
        var hostWindow = TimeSpan.FromMinutes(Math.Max(0, o.HostWindowMinutes));
        Prune(state.HandledResolves, v => v < since);
        Prune(state.MonitorQueuedAt, v => v < now - cooldown);
        Prune(state.CauseBypassAt, v => v < now - cooldown);
        foreach (var k in state.MonitorQueuedCause.Keys.Where(k => !state.MonitorQueuedAt.ContainsKey(k)).ToList()) state.MonitorQueuedCause.Remove(k);
        Prune(state.HostQueuedAt, v => v < now - hostWindow);
        // A host's chosen incident that expires unqueued hands over to the next waiting sibling.
        foreach (var expired in state.Deferred.Values.Where(e => e.FirstSeenUtc < since).ToList())
        {
            state.Deferred.Remove(expired.Key);
            if (state.HostSiblings.Remove(expired.SubjectId, out var siblings) && siblings.Count > 0)
            {
                var next = siblings[0] with { FirstSeenUtc = now };
                state.Deferred[next.Key] = next;
                if (siblings.Count > 1) state.HostSiblings[next.SubjectId] = siblings.Skip(1).ToList();
            }
        }
        foreach (var k in state.HostSiblings.Keys.Where(k => !state.Deferred.ContainsKey((EventKind.Opened, k)) && !state.Deferred.ContainsKey((EventKind.Resolved, k))).ToList())
            state.HostSiblings.Remove(k); // winner queued or gone
        Prune(state.QueuedIncidents, v => v < now.AddHours(-1));

        // ---- new events ----
        var events = new Dictionary<(EventKind, long), Event>(state.Deferred);
        void Add(Event e) => events.TryAdd(e.Key, e);

        if (!state.Initialized)
        {
            // First scan after start: recent events without a decision (the process may have stopped before them).
            var maxIncident = await db.Incidents.MaxAsync(i => (int?)i.Id, ct) ?? 0;
            var maxSignal = await db.AnomalySignals.MaxAsync(s => (long?)s.Id, ct) ?? 0;
            var opened = await db.Incidents.AsNoTracking()
                .Where(i => i.Id <= maxIncident && i.StartedAtUtc >= since && i.ResolvedAtUtc == null)
                .Select(i => new { i.Id, i.Monitor.CustomerId, i.MonitorId, i.StartedAtUtc }).ToListAsync(ct);
            var openedDone = await DecidedAsync(AiFeatures.IncidentInterpretation, AiSubjects.Incident, opened.Select(x => (long)x.Id), ct);
            foreach (var x in opened.Where(x => !openedDone.ContainsKey(x.Id)))
                Add(new Event(EventKind.Opened, x.Id, x.CustomerId, x.MonitorId, x.StartedAtUtc, now));
            var signals = await db.AnomalySignals.AsNoTracking()
                .Where(s => s.Id <= maxSignal && s.EndedAtUtc == null && s.UpdatedAtUtc >= since)
                .Select(s => new { s.Id, s.CustomerId, s.MonitorId, s.StartedAtUtc }).ToListAsync(ct);
            var signalsDone = await DecidedAsync(AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal, signals.Select(x => x.Id), ct);
            foreach (var x in signals.Where(x => !signalsDone.ContainsKey(x.Id)))
                Add(new Event(EventKind.Slowdown, x.Id, x.CustomerId, x.MonitorId, x.StartedAtUtc, now));
            state.LastIncidentId = maxIncident;
            state.LastSignalId = maxSignal;
            state.Initialized = true;
        }
        else
        {
            var last = state.LastIncidentId;
            var opened = await db.Incidents.AsNoTracking().Where(i => i.Id > last).OrderBy(i => i.Id).Take(BatchSize)
                .Select(i => new { i.Id, i.Monitor.CustomerId, i.MonitorId, i.StartedAtUtc }).ToListAsync(ct);
            foreach (var x in opened) Add(new Event(EventKind.Opened, x.Id, x.CustomerId, x.MonitorId, x.StartedAtUtc, now));
            if (opened.Count > 0) state.LastIncidentId = opened[^1].Id;
            var lastSignal = state.LastSignalId;
            var signals = await db.AnomalySignals.AsNoTracking().Where(s => s.Id > lastSignal).OrderBy(s => s.Id).Take(BatchSize)
                .Select(s => new { s.Id, s.CustomerId, s.MonitorId, s.StartedAtUtc }).ToListAsync(ct);
            foreach (var x in signals) Add(new Event(EventKind.Slowdown, x.Id, x.CustomerId, x.MonitorId, x.StartedAtUtc, now));
            if (signals.Count > 0) state.LastSignalId = signals[^1].Id;
        }

        // Resolved incidents (index on ResolvedAtUtc): a keyset watermark (ResolvedAtUtc, Id) over the settled part, plus
        // the last ResolvedOverlap re-read every scan, because ResolvedAtUtc is the probe start and the row is written
        // when the probe ends (a late commit can carry an earlier time). HandledResolves removes repeats.
        var settled = now - ResolvedOverlap;
        var (wAt, wId) = state.ResolvedWatermark is { } wm && wm.At >= since ? wm : (since, 0);
        var settledRows = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAtUtc != null && i.ResolvedAtUtc <= settled && (i.ResolvedAtUtc > wAt || (i.ResolvedAtUtc == wAt && i.Id > wId)))
            .OrderBy(i => i.ResolvedAtUtc).ThenBy(i => i.Id).Take(BatchSize)
            .Select(i => new { i.Id, i.Monitor.CustomerId, i.MonitorId, ResolvedAtUtc = i.ResolvedAtUtc!.Value })
            .ToListAsync(ct);
        if (settledRows.Count > 0) state.ResolvedWatermark = (settledRows[^1].ResolvedAtUtc, settledRows[^1].Id);
        var recentRows = await db.Incidents.AsNoTracking()
            .Where(i => i.ResolvedAtUtc != null && i.ResolvedAtUtc > settled)
            .OrderBy(i => i.ResolvedAtUtc).ThenBy(i => i.Id).Take(BatchSize)
            .Select(i => new { i.Id, i.Monitor.CustomerId, i.MonitorId, ResolvedAtUtc = i.ResolvedAtUtc!.Value })
            .ToListAsync(ct);
        foreach (var r in settledRows.Concat(recentRows).Where(r => !state.HandledResolves.ContainsKey(r.Id)))
            Add(new Event(EventKind.Resolved, r.Id, r.CustomerId, r.MonitorId, r.ResolvedAtUtc, now));

        // Decisions of the incidents involved: a resolve with a decision made after it is done; an opened event with
        // any decision is done (also covers deferred events whose job ran meanwhile).
        var incidentIds = events.Values.Where(e => e.Kind != EventKind.Slowdown).Select(e => e.SubjectId).Distinct().ToList();
        var incidentDecisions = await DecidedAsync(AiFeatures.IncidentInterpretation, AiSubjects.Incident, incidentIds, ct);
        var signalDecisions = await DecidedAsync(AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal,
            events.Values.Where(e => e.Kind == EventKind.Slowdown).Select(e => e.SubjectId), ct);
        foreach (var e in events.Values.ToList())
        {
            var done = e.Kind switch
            {
                EventKind.Resolved => incidentDecisions.TryGetValue(e.SubjectId, out var at) && at >= e.AtUtc,
                EventKind.Opened => incidentDecisions.ContainsKey(e.SubjectId),
                _ => signalDecisions.ContainsKey(e.SubjectId),
            };
            // An incident opened and resolved before it was queued needs one job only (it reads the resolved state).
            if (done || (e.Kind == EventKind.Opened && events.ContainsKey((EventKind.Resolved, e.SubjectId))))
            {
                events.Remove(e.Key);
                state.Deferred.Remove(e.Key);
                if (e.Kind == EventKind.Resolved) state.HandledResolves[(int)e.SubjectId] = e.AtUtc;
            }
        }
        if (events.Count == 0) return 0;

        // ---- eligibility (opt-in, plan, active) ----
        var customerIds = events.Values.Select(e => e.CustomerId).Distinct().ToList();
        var customers = (await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id) && c.IsActive && c.AiEnabled)
                .Select(c => new { c.Id, c.AiLanguage, c.Plan.AiFeatures, c.Plan.AiMonthlyCallLimit })
                .ToListAsync(ct))
            .Where(c => c.AiMonthlyCallLimit > 0)
            .ToDictionary(c => c.Id, c => new Eligible(AiJob.NormalizeLanguage(c.AiLanguage), AiFeatures.Parse(c.AiFeatures)));
        foreach (var e in events.Values.ToList())
            if (!customers.TryGetValue(e.CustomerId, out var c) || !c.Features.Contains(e.Feature))
                Drop(state, events, e);

        // ---- damping of AI-2: per monitor (flapping) and per host (correlated outages) ----
        var aiTwo = events.Values.Where(e => e.Kind != EventKind.Slowdown).ToList();
        var hosts = new Dictionary<int, string?>();
        var causes = new Dictionary<long, string>();
        var bypassIds = new HashSet<long>();
        if (aiTwo.Count > 0)
        {
            var monitorIds = aiTwo.Select(e => e.MonitorId).Distinct().ToList();
            hosts = (await db.Monitors.AsNoTracking().Where(m => monitorIds.Contains(m.Id))
                    .Select(m => new { m.Id, m.Type, m.Target }).ToListAsync(ct))
                .ToDictionary(m => m.Id, m => AiTenantData.HostOf(m.Type, m.Target));

            // Monitors / hosts with an AI-2 decision recently (also from before a restart).
            var horizon = now - (cooldown > hostWindow ? cooldown : hostWindow);
            var recent = await db.AiDecisions.AsNoTracking()
                .Where(d => d.Feature == AiFeatures.IncidentInterpretation && d.SubjectType == AiSubjects.Incident
                            && customerIds.Contains(d.CustomerId) && d.CreatedAtUtc >= horizon)
                .Select(d => new { d.SubjectId, d.CreatedAtUtc }).ToListAsync(ct);
            var recentIds = recent.Select(r => int.TryParse(r.SubjectId, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            var recentIncidents = recentIds.Count == 0 ? [] : await db.Incidents.AsNoTracking().Where(i => recentIds.Contains(i.Id))
                .Select(i => new { i.Id, i.MonitorId, i.Monitor.CustomerId, i.Monitor.Type, i.Monitor.Target, i.Cause }).ToListAsync(ct);
            var monitorLast = new Dictionary<int, DateTime>(state.MonitorQueuedAt);
            var monitorCause = new Dictionary<int, string>(state.MonitorQueuedCause);
            var hostLast = new Dictionary<(int, string), DateTime>(state.HostQueuedAt);
            foreach (var r in recent.OrderBy(r => r.CreatedAtUtc))
            {
                var inc = recentIncidents.FirstOrDefault(i => i.Id.ToString(CultureInfo.InvariantCulture) == r.SubjectId);
                if (inc is null) continue;
                if (!monitorLast.TryGetValue(inc.MonitorId, out var at) || at <= r.CreatedAtUtc)
                {
                    monitorLast[inc.MonitorId] = r.CreatedAtUtc;
                    monitorCause[inc.MonitorId] = CauseKey(inc.Cause);
                }
                if (AiTenantData.HostOf(inc.Type, inc.Target) is { } h) Max(hostLast, (inc.CustomerId, h), r.CreatedAtUtc);
            }
            var eventIds = aiTwo.Select(e => (int)e.SubjectId).Distinct().ToList();
            causes = (await db.Incidents.AsNoTracking().Where(i => eventIds.Contains(i.Id)).Select(i => new { i.Id, i.Cause }).ToListAsync(ct))
                .ToDictionary(i => (long)i.Id, i => CauseKey(i.Cause));
            var scanWinner = new Dictionary<(int, string), Event>();
            var bypassedThisScan = new HashSet<int>();

            foreach (var e in aiTwo.OrderBy(e => e.SubjectId))
            {
                var ownDecision = incidentDecisions.ContainsKey(e.SubjectId) || state.QueuedIncidents.ContainsKey(e.SubjectId);
                if (e.Kind == EventKind.Resolved && ownDecision) continue; // the summary of an interpreted incident is updated
                var host = hosts.GetValueOrDefault(e.MonitorId);
                var cause = causes.GetValueOrDefault(e.SubjectId, "");
                // Same monitor, same (normalised) cause within the cool-down: flapping. A different cause is news.
                var monitorWithinCooldown = monitorLast.TryGetValue(e.MonitorId, out var ml) && ml >= now - cooldown;
                // A changed cause is news, but only once per cool-down (a monitor alternating causes is still flapping).
                var causeChanged = monitorWithinCooldown && monitorCause.GetValueOrDefault(e.MonitorId, "") != cause
                                   && !state.CauseBypassAt.ContainsKey(e.MonitorId) && !bypassedThisScan.Contains(e.MonitorId);
                if (causeChanged)
                {
                    bypassedThisScan.Add(e.MonitorId);
                    bypassIds.Add(e.SubjectId);
                }
                var monitorRecent = monitorWithinCooldown && !causeChanged;
                if (host is not null && !monitorRecent && scanWinner.TryGetValue((e.CustomerId, host), out var winner))
                {
                    // Waits behind the incident chosen for its host in this scan (promoted if that one expires unqueued).
                    events.Remove(e.Key);
                    state.Deferred.Remove(e.Key);
                    if (!state.HostSiblings.TryGetValue(winner.SubjectId, out var list)) state.HostSiblings[winner.SubjectId] = list = [];
                    if (list.All(x => x.Key != e.Key)) list.Add(e);
                    continue;
                }
                // A changed cause on this monitor is interpreted even inside the host window (that window is for other monitors).
                var hostRecent = !causeChanged && host is not null && hostLast.TryGetValue((e.CustomerId, host), out var hl) && hl >= now - hostWindow;
                if (monitorRecent || hostRecent)
                {
                    Drop(state, events, e);
                    continue;
                }
                // The first incident of this host in this scan wins; later ones wait behind it.
                monitorLast[e.MonitorId] = now;
                monitorCause[e.MonitorId] = cause;
                if (host is not null) scanWinner[(e.CustomerId, host)] = e;
            }
        }

        // ---- fair scheduling: round-robin across customers, capped per customer ----
        var perCustomer = events.Values.GroupBy(e => e.CustomerId).OrderBy(g => g.Key)
            .Select(g => new Queue<Event>(g.OrderBy(e => e.AtUtc).ThenBy(e => e.SubjectId))).ToList();
        return EnqueueFairly(state, perCustomer, customers, hosts, causes, bypassIds, Math.Max(1, o.MaxJobsPerCustomerPerScan), now);
    }

    /// <summary>Normalised incident cause (numbers, ids and masked values unified) for the cool-down comparison.</summary>
    public static string CauseKey(string? cause) => string.IsNullOrWhiteSpace(cause) ? "" : IncidentInterpretationProvider.NormalizeError(cause);

    private int EnqueueFairly(State state, List<Queue<Event>> perCustomer, Dictionary<int, Eligible> customers,
        Dictionary<int, string?> hosts, Dictionary<long, string> causes, HashSet<long> bypassIds, int cap, DateTime now)
    {
        var queued = 0;
        var queueFull = false;
        for (var round = 0; round < cap && !queueFull; round++)
        {
            foreach (var q in perCustomer)
            {
                if (!q.TryPeek(out var e)) continue;
                var job = new AiJob(e.Feature, e.CustomerId, e.SubjectType, e.Subject, customers[e.CustomerId].Language);
                if (!queue.TryEnqueue(job))
                {
                    queueFull = true; // keep it (and the rest) for the next scan
                    break;
                }
                q.Dequeue();
                queued++;
                state.Deferred.Remove(e.Key);
                if (e.Kind == EventKind.Resolved) state.HandledResolves[(int)e.SubjectId] = e.AtUtc;
                if (e.Kind != EventKind.Slowdown)
                {
                    state.QueuedIncidents[e.SubjectId] = now;
                    state.MonitorQueuedAt[e.MonitorId] = now;
                    state.MonitorQueuedCause[e.MonitorId] = causes.GetValueOrDefault(e.SubjectId, "");
                    if (bypassIds.Contains(e.SubjectId)) state.CauseBypassAt[e.MonitorId] = now;
                    state.HostSiblings.Remove(e.SubjectId); // the host's outage is interpreted
                    if (hosts.GetValueOrDefault(e.MonitorId) is { } h) state.HostQueuedAt[(e.CustomerId, h)] = now;
                }
            }
        }
        // Over the cap or refused: retried next scan.
        foreach (var e in perCustomer.SelectMany(q => q))
            state.Deferred[e.Key] = e;
        if (state.Deferred.Count > MaxDeferred)
            foreach (var old in state.Deferred.Values.OrderBy(e => e.FirstSeenUtc).Take(state.Deferred.Count - MaxDeferred).ToList())
                state.Deferred.Remove(old.Key);

        if (queued > 0) logger.LogDebug("Queued {Count} AI job(s) for incidents / slowdowns ({Deferred} deferred)", queued, state.Deferred.Count);
        return queued;
    }

    /// <summary>Not interpreted (ineligible or damped): forgotten, and a resolve is not looked at again.</summary>
    private static void Drop(State state, Dictionary<(EventKind, long), Event> events, Event e)
    {
        events.Remove(e.Key);
        state.Deferred.Remove(e.Key);
        if (e.Kind == EventKind.Resolved) state.HandledResolves[(int)e.SubjectId] = e.AtUtc;
    }

    /// <summary>Subject id → newest decision time, for subjects that have any decision.</summary>
    private async Task<Dictionary<long, DateTime>> DecidedAsync(string feature, string subjectType, IEnumerable<long> ids, CancellationToken ct)
    {
        var keys = ids.Select(i => i.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
        if (keys.Count == 0) return [];
        return (await db.AiDecisions.AsNoTracking()
                .Where(d => d.Feature == feature && d.SubjectType == subjectType && keys.Contains(d.SubjectId))
                .GroupBy(d => d.SubjectId)
                .Select(g => new { g.Key, At = g.Max(d => d.CreatedAtUtc) })
                .ToListAsync(ct))
            .ToDictionary(x => long.Parse(x.Key, CultureInfo.InvariantCulture), x => x.At);
    }

    private static void Prune<TKey, TValue>(Dictionary<TKey, TValue> d, Func<TValue, bool> expired) where TKey : notnull
    {
        foreach (var k in d.Where(kv => expired(kv.Value)).Select(kv => kv.Key).ToList()) d.Remove(k);
    }

    private static void Max<TKey>(Dictionary<TKey, DateTime> d, TKey key, DateTime value) where TKey : notnull
    {
        if (!d.TryGetValue(key, out var v) || v < value) d[key] = value;
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
