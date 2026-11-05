using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Features;

/// <summary>
/// One pass of the AI-5 suggestion job over every enabled, not paused, non-heartbeat monitor of an active customer that
/// opted in to AI and whose plan includes <see cref="AiFeatures.SettingsSuggestions"/> (AI-10):
/// <list type="number">
/// <item>reads the monitor's central checks (outside maintenance, confirmation probes excluded) and incidents of the
/// last <see cref="AiSuggestionOptions.WindowDays"/> days, but never from before the monitor's last settings change
/// (<see cref="Domain.Entities.Monitor.SettingsChangedAtUtc"/>), so an applied or edited setting is judged only on its
/// own history (no ratchet). Without a successful check it also reads the whole history since that change. Then it runs
/// <see cref="SettingsSuggestionRules"/> (L1, no LLM);</item>
/// <item>creates an Open suggestion for a new finding, unless the same kind was dismissed for the monitor within
/// <see cref="AiSuggestionOptions.DismissDays"/> days or applied within the window. An open suggestion whose finding
/// still holds with the same current value and (nearly) the same proposal keeps its row; its evidence is refreshed in
/// place once per window. It is closed as Obsolete when the finding is gone or the setting / proposal changed. Status
/// changes use conditional updates (WHERE Status = Open), so a concurrent Apply / Dismiss by a user always wins;</item>
/// <item>closes open suggestions of customers that are no longer eligible (opt-out, plan, call limit) and of monitors
/// that are paused, disabled or heartbeats;</item>
/// <item>queues the AI explanation (an <see cref="AiDecision"/> through <see cref="IAiJobQueue"/>) for open suggestions
/// without one, with evidence newer than their explanation, or whose last attempt failed / was skipped for budget more
/// than <see cref="AiOptions.RetryCooldownMinutes"/> ago; only when AI is available.</item>
/// </list>
/// Never changes a monitor: applying is a CustomerAdmin's click through the normal monitor validation (A1).
/// </summary>
public sealed class SettingsSuggestionJob(AppDbContext db, IAiJobQueue queue, IAiClient client, IOptions<AiOptions> options, TimeProvider time,
    ILogger<SettingsSuggestionJob> logger)
{
    public const int MaxQueuedPerRun = 200;
    /// <summary>A new timeout proposal closer than this to the open one keeps the open suggestion (no churn).</summary>
    public const int TimeoutProposalBandSeconds = 2;

    private static readonly AiDecisionStatus[] Retryable =
        [AiDecisionStatus.Failed, AiDecisionStatus.Truncated, AiDecisionStatus.SkippedBudget, AiDecisionStatus.SkippedOptOut, AiDecisionStatus.SkippedPlan];

    public sealed record RunSummary(int Monitors, int Created, int Closed, int Queued);

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value.Suggestions;
        var now = time.GetUtcNow().UtcDateTime;
        var windowDays = Math.Max(1, o.WindowDays);

        var customers = (await db.Customers.AsNoTracking()
                .Where(c => c.IsActive && c.AiEnabled)
                .Select(c => new { c.Id, c.AiLanguage, c.Plan.AiFeatures, c.Plan.AiMonthlyCallLimit })
                .ToListAsync(ct))
            .Where(c => c.AiMonthlyCallLimit > 0 && AiFeatures.Parse(c.AiFeatures).Contains(AiFeatures.SettingsSuggestions))
            .ToDictionary(c => c.Id, c => AiJob.NormalizeLanguage(c.AiLanguage));
        var customerIds = customers.Keys.ToList();

        int monitors = 0, created = 0, closed = 0;
        var lastId = 0;
        while (customerIds.Count > 0)
        {
            var batch = await db.Monitors.AsNoTracking()
                .Where(m => m.Id > lastId && customerIds.Contains(m.CustomerId) && m.IsEnabled && m.Status != MonitorStatus.Paused && m.Type != MonitorType.Heartbeat)
                .OrderBy(m => m.Id)
                .Take(Math.Max(1, o.BatchSize))
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var m in batch)
            {
                monitors++;
                var detected = await DetectAsync(m, now, windowDays, o, ct);
                var (c, x) = await ReconcileAsync(m, detected, now, windowDays, o, ct);
                created += c;
                closed += x;
            }
            db.ChangeTracker.Clear();
        }

        // Customers that are no longer eligible (opted out, plan without AI-5, no AI calls, suspended) keep nothing open.
        var openCustomers = await db.SettingsSuggestions.Where(s => s.Status == SettingsSuggestionStatus.Open)
            .Select(s => s.CustomerId).Distinct().ToListAsync(ct);
        var ineligible = openCustomers.Where(id => !customers.ContainsKey(id)).ToList();
        if (ineligible.Count > 0)
            closed += await db.SettingsSuggestions
                .Where(s => s.Status == SettingsSuggestionStatus.Open && ineligible.Contains(s.CustomerId))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SettingsSuggestionStatus.Obsolete).SetProperty(s => s.ClosedAtUtc, now), ct);

        // Monitors that are paused, disabled or turned into heartbeats keep no open suggestion (deleted ones cascade).
        closed += await db.SettingsSuggestions
            .Where(s => s.Status == SettingsSuggestionStatus.Open
                        && !db.Monitors.Any(m => m.Id == s.MonitorId && m.IsEnabled && m.Status != MonitorStatus.Paused && m.Type != MonitorType.Heartbeat))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SettingsSuggestionStatus.Obsolete).SetProperty(s => s.ClosedAtUtc, now), ct);

        var queued = await QueueExplanationsAsync(customers, now, ct);
        if (created + closed + queued > 0)
            logger.LogInformation("Settings suggestions: {Created} created, {Closed} closed, {Queued} explanation(s) queued ({Monitors} monitors)",
                created, closed, queued, monitors);
        return new RunSummary(monitors, created, closed, queued);
    }

    private async Task<List<DetectedSuggestion>> DetectAsync(Domain.Entities.Monitor m, DateTime now, int windowDays, AiSuggestionOptions o, CancellationToken ct)
    {
        var settings = SettingsSuggestionRules.SettingsOf(m);
        var settingsSince = settings.CurrentSettingsSinceUtc;
        var windowStart = now.AddDays(-windowDays);
        var since = settingsSince > windowStart ? settingsSince : windowStart;
        // Evidence says "in the last N days": the days actually covered (at most the window).
        var days = Math.Clamp((int)Math.Ceiling((now - since).TotalDays), 1, windowDays);

        var checks = (await db.CheckResults.AsNoTracking()
                .Where(r => r.MonitorId == m.Id && r.RegionId == null && !r.InMaintenance && !r.IsConfirmation && r.CheckedAtUtc >= since) // FEAT-6: central only
                .OrderByDescending(r => r.CheckedAtUtc)
                .Take(Math.Max(1, o.MaxChecksPerMonitor))
                .Select(r => new SettingsSuggestionRules.Check(r.CheckedAtUtc, r.IsUp, r.StatusCode, r.ResponseTimeMs, r.Error))
                .ToListAsync(ct))
            .OrderBy(c => c.AtUtc).ToList();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == m.Id && i.StartedAtUtc >= since)
            .Select(i => new SettingsSuggestionRules.IncidentRow(i.StartedAtUtc, i.ResolvedAtUtc, i.ResolvedDuringMaintenance))
            .ToListAsync(ct);

        SettingsSuggestionRules.NeverUpHistory? neverUp = null;
        if (checks.Count > 0 && !checks.Any(c => c.IsUp)
            && !await db.CheckResults.AnyAsync(r => r.MonitorId == m.Id && r.RegionId == null && r.IsUp && r.CheckedAtUtc >= settingsSince, ct))
        {
            var all = db.CheckResults.AsNoTracking().Where(r => r.MonitorId == m.Id && r.RegionId == null && !r.IsConfirmation && r.CheckedAtUtc >= settingsSince);
            var first = await all.MinAsync(r => r.CheckedAtUtc, ct);
            var count = await all.CountAsync(ct);
            var top = await all.Where(r => r.Error != null).GroupBy(r => r.Error)
                .Select(g => new { Error = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count).FirstOrDefaultAsync(ct);
            var keywordMisses = m.Type == MonitorType.Keyword
                ? await all.CountAsync(r => r.Error != null && r.Error.StartsWith("Keyword '") && r.Error.EndsWith("' not found in response"), ct)
                : 0;
            neverUp = new(first, count, top?.Error, top?.Count ?? 0, keywordMisses);
        }
        return SettingsSuggestionRules.Evaluate(settings, checks, incidents, neverUp, now, days);
    }

    /// <summary>Same finding: equal values, or a timeout proposal within <see cref="TimeoutProposalBandSeconds"/>.</summary>
    public static bool SameProposal(SettingsSuggestionKind kind, string? open, string? fresh)
    {
        if (open == fresh) return true;
        return kind == SettingsSuggestionKind.RaiseTimeout
               && int.TryParse(open, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
               && int.TryParse(fresh, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
               && Math.Abs(a - b) < TimeoutProposalBandSeconds;
    }

    private async Task<(int Created, int Closed)> ReconcileAsync(Domain.Entities.Monitor m, List<DetectedSuggestion> detected, DateTime now,
        int windowDays, AiSuggestionOptions o, CancellationToken ct)
    {
        var dismissCutoff = now.AddDays(-Math.Max(0, o.DismissDays));
        var appliedCutoff = now.AddDays(-windowDays);
        IQueryable<SettingsSuggestion> Blocking() => db.SettingsSuggestions.AsNoTracking().Where(s => s.MonitorId == m.Id
            && (s.Status == SettingsSuggestionStatus.Open
                || (s.Status == SettingsSuggestionStatus.Dismissed && s.ClosedAtUtc >= dismissCutoff)
                || (s.Status == SettingsSuggestionStatus.Applied && s.ClosedAtUtc >= appliedCutoff)));
        var existing = await Blocking().ToListAsync(ct);

        int created = 0, closed = 0;
        foreach (var kind in Enum.GetValues<SettingsSuggestionKind>())
        {
            var d = detected.FirstOrDefault(x => x.Kind == kind);
            // Dismissed recently, or applied within the window (its effect needs fresh history first).
            var suppressed = existing.Any(s => s.Kind == kind && s.Status != SettingsSuggestionStatus.Open);
            var open = existing.FirstOrDefault(s => s.Kind == kind && s.Status == SettingsSuggestionStatus.Open);
            if (open is not null)
            {
                if (d is null || suppressed || open.CurrentValue != d.CurrentValue || !SameProposal(kind, open.SuggestedValue, d.SuggestedValue))
                {
                    closed += await db.SettingsSuggestions.Where(s => s.Id == open.Id && s.Status == SettingsSuggestionStatus.Open)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SettingsSuggestionStatus.Obsolete).SetProperty(s => s.ClosedAtUtc, now), ct);
                }
                else
                {
                    // Still valid: keep the row (id, explanation, feedback, the value an admin may be applying) and
                    // refresh its evidence about once per window.
                    if (open.EvidenceUpdatedAtUtc <= now.AddDays(-windowDays))
                    {
                        var json = SuggestionFact.Serialize(d.Evidence);
                        await db.SettingsSuggestions.Where(s => s.Id == open.Id && s.Status == SettingsSuggestionStatus.Open)
                            .ExecuteUpdateAsync(u => u.SetProperty(s => s.EvidenceJson, json).SetProperty(s => s.EvidenceUpdatedAtUtc, now), ct);
                    }
                    continue;
                }
            }
            if (d is null || suppressed) continue;
            // Re-check right before inserting: a user may have dismissed or applied it since it was read.
            if (await Blocking().AnyAsync(s => s.Kind == kind, ct)) continue;
            db.SettingsSuggestions.Add(new SettingsSuggestion
            {
                CustomerId = m.CustomerId, MonitorId = m.Id, Kind = d.Kind, Status = SettingsSuggestionStatus.Open,
                CurrentValue = d.CurrentValue, SuggestedValue = d.SuggestedValue,
                EvidenceJson = SuggestionFact.Serialize(d.Evidence), CreatedAtUtc = now, EvidenceUpdatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            created++;
        }
        return (created, closed);
    }

    /// <summary>
    /// Open suggestions of eligible customers that need an explanation: none yet (new, or dropped by a full queue), one
    /// older than the evidence, or a last attempt that failed or was skipped (budget, opt-in, plan) more than the retry
    /// cool-down ago.
    /// </summary>
    private async Task<int> QueueExplanationsAsync(Dictionary<int, string> customers, DateTime now, CancellationToken ct)
    {
        if (!options.Value.Enabled || !client.IsAvailable || customers.Count == 0) return 0;
        var ids = customers.Keys.ToList();
        var open = await db.SettingsSuggestions.AsNoTracking()
            .Where(s => s.Status == SettingsSuggestionStatus.Open && ids.Contains(s.CustomerId))
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.CustomerId, s.EvidenceUpdatedAtUtc })
            .ToListAsync(ct);
        if (open.Count == 0) return 0;
        var subjects = open.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var latest = (await db.AiDecisions.AsNoTracking()
                .Where(d => d.Feature == AiFeatures.SettingsSuggestions && d.SubjectType == SettingsSuggestionProvider.SubjectType && subjects.Contains(d.SubjectId))
                .Select(d => new { d.Id, d.SubjectId, d.Status, d.CreatedAtUtc })
                .ToListAsync(ct))
            .GroupBy(d => d.SubjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Id).First());
        var retryBefore = now.AddMinutes(-Math.Max(1, options.Value.RetryCooldownMinutes));

        var queued = 0;
        foreach (var s in open)
        {
            if (queued >= MaxQueuedPerRun) break;
            var subject = s.Id.ToString(CultureInfo.InvariantCulture);
            if (latest.TryGetValue(subject, out var last))
            {
                var stale = last.CreatedAtUtc < s.EvidenceUpdatedAtUtc && last.Status != AiDecisionStatus.Pending;
                var retry = Retryable.Contains(last.Status) && last.CreatedAtUtc <= retryBefore;
                if (!stale && !retry) continue;
            }
            if (queue.TryEnqueue(new AiJob(AiFeatures.SettingsSuggestions, s.CustomerId, SettingsSuggestionProvider.SubjectType, subject, customers[s.CustomerId])))
                queued++;
        }
        return queued;
    }
}

/// <summary>
/// Runs <see cref="SettingsSuggestionJob"/> shortly after start and then every <see cref="AiSuggestionOptions.IntervalMinutes"/>.
/// Off when Ai:Suggestions:Enabled or Monitoring:Enabled is false. Detection needs no LLM, so it is independent of Ai:Enabled.
/// </summary>
public sealed class SettingsSuggestionHostedService(
    IServiceScopeFactory scopes,
    IOptions<AiOptions> options,
    IConfiguration config,
    TimeProvider time,
    ILogger<SettingsSuggestionHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Suggestions;
        if (!o.Enabled || !config.GetValue("Monitoring:Enabled", true)) return;
        try
        {
            await Task.Delay(StartDelay, time, stoppingToken);
            await RunOnceAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.IntervalMinutes)), time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SettingsSuggestionJob>().RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Settings suggestion job failed");
        }
    }
}
