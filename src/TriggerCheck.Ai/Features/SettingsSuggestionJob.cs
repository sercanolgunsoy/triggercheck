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
/// <item>reads the monitor's central checks outside maintenance and its incidents of the last
/// <see cref="AiSuggestionOptions.WindowDays"/> days (plus, when there is no successful check, the whole history) and
/// runs <see cref="SettingsSuggestionRules"/> (L1, no LLM);</item>
/// <item>creates an Open suggestion for a new finding, unless the same kind was dismissed for the monitor within
/// <see cref="AiSuggestionOptions.DismissDays"/> days; an open one is closed as Obsolete when its condition is gone, the
/// setting or the proposal changed, or it is older than the window (a fresh one replaces it);</item>
/// <item>queues the AI explanation (an <see cref="AiDecision"/> through <see cref="IAiJobQueue"/>) for open suggestions
/// without one, only when AI is available. Budget, opt-in and plan are checked again by the interpreter.</item>
/// </list>
/// Never changes a monitor: applying is a CustomerAdmin's click through the normal monitor validation (A1).
/// </summary>
public sealed class SettingsSuggestionJob(AppDbContext db, IAiJobQueue queue, IAiClient client, IOptions<AiOptions> options, TimeProvider time,
    ILogger<SettingsSuggestionJob> logger)
{
    public const int MaxQueuedPerRun = 200;

    public sealed record RunSummary(int Monitors, int Created, int Closed, int Queued);

    public async Task<RunSummary> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value.Suggestions;
        var now = time.GetUtcNow().UtcDateTime;
        var windowDays = Math.Max(1, o.WindowDays);
        var windowStart = now.AddDays(-windowDays);
        var dismissCutoff = now.AddDays(-Math.Max(0, o.DismissDays));

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
                var detected = await DetectAsync(m, windowStart, now, windowDays, o, ct);
                var (c, x) = await ReconcileAsync(m, detected, now, windowStart, dismissCutoff, ct);
                created += c;
                closed += x;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // Monitors that are paused, disabled or turned into heartbeats keep no open suggestion (deleted ones cascade).
        closed += await db.SettingsSuggestions
            .Where(s => s.Status == SettingsSuggestionStatus.Open
                        && !db.Monitors.Any(m => m.Id == s.MonitorId && m.IsEnabled && m.Status != MonitorStatus.Paused && m.Type != MonitorType.Heartbeat))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SettingsSuggestionStatus.Obsolete).SetProperty(s => s.ClosedAtUtc, now), ct);

        var queued = await QueueExplanationsAsync(customers, ct);
        if (created + closed + queued > 0)
            logger.LogInformation("Settings suggestions: {Created} created, {Closed} closed, {Queued} explanation(s) queued ({Monitors} monitors)",
                created, closed, queued, monitors);
        return new RunSummary(monitors, created, closed, queued);
    }

    private async Task<List<DetectedSuggestion>> DetectAsync(Domain.Entities.Monitor m, DateTime windowStart, DateTime now, int windowDays,
        AiSuggestionOptions o, CancellationToken ct)
    {
        var checks = (await db.CheckResults.AsNoTracking()
                .Where(r => r.MonitorId == m.Id && r.RegionId == null && !r.InMaintenance && r.CheckedAtUtc >= windowStart) // FEAT-6: central only
                .OrderByDescending(r => r.CheckedAtUtc)
                .Take(Math.Max(1, o.MaxChecksPerMonitor))
                .Select(r => new SettingsSuggestionRules.Check(r.CheckedAtUtc, r.IsUp, r.StatusCode, r.ResponseTimeMs, r.Error))
                .ToListAsync(ct))
            .OrderBy(c => c.AtUtc).ToList();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == m.Id && i.StartedAtUtc >= windowStart)
            .Select(i => new SettingsSuggestionRules.IncidentRow(i.StartedAtUtc, i.ResolvedAtUtc, i.ResolvedDuringMaintenance))
            .ToListAsync(ct);

        SettingsSuggestionRules.NeverUpHistory? neverUp = null;
        if (checks.Count > 0 && !checks.Any(c => c.IsUp)
            && !await db.CheckResults.AnyAsync(r => r.MonitorId == m.Id && r.RegionId == null && r.IsUp, ct))
        {
            var all = db.CheckResults.AsNoTracking().Where(r => r.MonitorId == m.Id && r.RegionId == null);
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
        return SettingsSuggestionRules.Evaluate(SettingsSuggestionRules.SettingsOf(m), checks, incidents, neverUp, now, windowDays);
    }

    private async Task<(int Created, int Closed)> ReconcileAsync(Domain.Entities.Monitor m, List<DetectedSuggestion> detected, DateTime now,
        DateTime windowStart, DateTime dismissCutoff, CancellationToken ct)
    {
        var existing = await db.SettingsSuggestions
            .Where(s => s.MonitorId == m.Id && (s.Status == SettingsSuggestionStatus.Open
                                                || (s.Status == SettingsSuggestionStatus.Dismissed && s.ClosedAtUtc >= dismissCutoff)))
            .ToListAsync(ct);
        int created = 0, closed = 0;
        foreach (var kind in Enum.GetValues<SettingsSuggestionKind>())
        {
            var d = detected.FirstOrDefault(x => x.Kind == kind);
            var dismissed = existing.Any(s => s.Kind == kind && s.Status == SettingsSuggestionStatus.Dismissed);
            var open = existing.FirstOrDefault(s => s.Kind == kind && s.Status == SettingsSuggestionStatus.Open);
            if (open is not null && (d is null || dismissed || open.CurrentValue != d.CurrentValue || open.SuggestedValue != d.SuggestedValue
                                     || open.CreatedAtUtc <= windowStart))
            {
                open.Status = SettingsSuggestionStatus.Obsolete;
                open.ClosedAtUtc = now;
                open = null;
                closed++;
            }
            if (d is null || open is not null || dismissed) continue;
            db.SettingsSuggestions.Add(new SettingsSuggestion
            {
                CustomerId = m.CustomerId, MonitorId = m.Id, Kind = d.Kind, Status = SettingsSuggestionStatus.Open,
                CurrentValue = d.CurrentValue, SuggestedValue = d.SuggestedValue,
                EvidenceJson = SuggestionFact.Serialize(d.Evidence), CreatedAtUtc = now
            });
            created++;
        }
        return (created, closed);
    }

    /// <summary>Open suggestions of eligible customers that have no AI decision yet (new ones, or ones dropped by a full queue).</summary>
    private async Task<int> QueueExplanationsAsync(Dictionary<int, string> customers, CancellationToken ct)
    {
        if (!options.Value.Enabled || !client.IsAvailable || customers.Count == 0) return 0;
        var ids = customers.Keys.ToList();
        var open = await db.SettingsSuggestions.AsNoTracking()
            .Where(s => s.Status == SettingsSuggestionStatus.Open && ids.Contains(s.CustomerId))
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.CustomerId })
            .ToListAsync(ct);
        if (open.Count == 0) return 0;
        var subjects = open.Select(s => s.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var decided = (await db.AiDecisions.AsNoTracking()
                .Where(d => d.Feature == AiFeatures.SettingsSuggestions && d.SubjectType == SettingsSuggestionProvider.SubjectType && subjects.Contains(d.SubjectId))
                .Select(d => d.SubjectId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var queued = 0;
        foreach (var s in open)
        {
            if (queued >= MaxQueuedPerRun) break;
            var subject = s.Id.ToString(CultureInfo.InvariantCulture);
            if (decided.Contains(subject)) continue;
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
