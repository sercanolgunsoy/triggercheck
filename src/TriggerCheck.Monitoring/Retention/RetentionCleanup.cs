using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Retention;

/// <summary>
/// Deletes data older than the owning customer's plan RetentionDays (RetentionDays &lt;= 0 = keep forever):
/// CheckResults, resolved Incidents (by resolution time; open incidents are never deleted) and Notifications.
/// Notifications of deleted monitors are removed regardless of age. SMS / voice usage counters (one row per customer
/// and UTC day, only needed for today's limit) are kept for <see cref="SmsVoiceUsageDays"/> days.
/// AI records follow the same retention: AI decisions (never those of the current month, which the AI budget counts),
/// closed anomaly signals and closed settings suggestions (kept at least Ai:Suggestions:DismissDays + 1 days, so a
/// dismissal suppresses its kind for the whole period). Platform AI decisions without a customer (AI-12) are kept
/// <see cref="PlatformAiDecisionDays"/> days.
/// </summary>
public sealed class RetentionCleaner(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<RetentionCleaner> logger,
    Microsoft.Extensions.Configuration.IConfiguration? config = null)
{
    public const int SmsVoiceUsageDays = 35;
    /// <summary>AI decisions without a customer (AI-12 platform insight summaries) are kept this many days.</summary>
    public const int PlatformAiDecisionDays = 400;
    /// <summary>Default of Ai:Suggestions:DismissDays (the option itself lives in the AI layer, which this project does not reference).</summary>
    public const int DefaultSuggestionDismissDays = 30;

    /// <summary>Closed AI-5 suggestions are kept at least this many days: the dismissal period plus one.</summary>
    public int SuggestionKeepDays =>
        Math.Max(0, config is null ? DefaultSuggestionDismissDays
            : Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(config, "Ai:Suggestions:DismissDays", DefaultSuggestionDismissDays)) + 1;

    public async Task<int> CleanupAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var groups = (await db.Customers.AsNoTracking()
                .Select(c => new { c.Id, c.Plan.RetentionDays })
                .ToListAsync(ct))
            .Where(x => x.RetentionDays > 0)
            .GroupBy(x => x.RetentionDays, x => x.Id);

        int results = 0, incidents = 0, notifications = 0, ai = 0;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var g in groups)
        {
            var cutoff = now.AddDays(-g.Key);
            var customerIds = g.ToList();
            results += await db.CheckResults
                .Where(r => r.CheckedAtUtc < cutoff && customerIds.Contains(r.Monitor.CustomerId))
                .ExecuteDeleteAsync(ct);
            incidents += await db.Incidents
                .Where(i => i.ResolvedAtUtc != null && i.ResolvedAtUtc < cutoff && customerIds.Contains(i.Monitor.CustomerId))
                .ExecuteDeleteAsync(ct);
            // Notifications reference the monitor without a navigation property.
            notifications += await db.Notifications
                .Where(n => n.SentAtUtc < cutoff && db.Monitors.Any(m => m.Id == n.MonitorId && customerIds.Contains(m.CustomerId)))
                .ExecuteDeleteAsync(ct);
            var aiCutoff = cutoff < monthStart ? cutoff : monthStart;
            ai += await db.AiDecisions
                .Where(d => d.CreatedAtUtc < aiCutoff && d.CustomerId != null && customerIds.Contains(d.CustomerId.Value))
                .ExecuteDeleteAsync(ct);
            ai += await db.AnomalySignals
                .Where(a => a.EndedAtUtc != null && a.EndedAtUtc < cutoff && customerIds.Contains(a.CustomerId))
                .ExecuteDeleteAsync(ct);
            // AI-5: closed suggestions; a dismissal is kept at least as long as it suppresses the suggestion.
            var keepFrom = now.AddDays(-SuggestionKeepDays);
            var suggestionCutoff = cutoff < keepFrom ? cutoff : keepFrom;
            ai += await db.SettingsSuggestions
                .Where(x => x.ClosedAtUtc != null && x.ClosedAtUtc < suggestionCutoff && customerIds.Contains(x.CustomerId))
                .ExecuteDeleteAsync(ct);
        }

        // AI-12: platform-level decisions (no customer, SuperAdmin insight summaries) are kept for a fixed period.
        var platformCutoff = now.AddDays(-PlatformAiDecisionDays);
        ai += await db.AiDecisions.Where(d => d.CustomerId == null && d.CreatedAtUtc < platformCutoff).ExecuteDeleteAsync(ct);

        // Notifications of monitors that no longer exist (they have no FK, so no cascade).
        notifications += await db.Notifications
            .Where(n => !db.Monitors.Any(m => m.Id == n.MonitorId))
            .ExecuteDeleteAsync(ct);

        var usageCutoff = now.Date.AddDays(-SmsVoiceUsageDays);
        var usage = await db.SmsVoiceUsage.Where(u => u.DayUtc < usageCutoff).ExecuteDeleteAsync(ct);

        var total = results + incidents + notifications + usage + ai;
        if (total > 0)
            logger.LogInformation("Retention cleanup deleted {Results} check result(s), {Incidents} incident(s), {Notifications} notification(s), {Usage} SMS/voice usage row(s), {Ai} AI record(s)",
                results, incidents, notifications, usage, ai);
        return total;
    }
}

/// <summary>Runs <see cref="RetentionCleaner"/> at startup and then every <see cref="MonitoringOptions.RetentionCleanupMinutes"/>. Off when Monitoring:Enabled=false.</summary>
public sealed class RetentionCleanupService(
    IServiceScopeFactory scopes,
    IOptions<MonitoringOptions> options,
    TimeProvider time,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, o.RetentionCleanupMinutes)), time);
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionCleaner>().CleanupAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retention cleanup failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
