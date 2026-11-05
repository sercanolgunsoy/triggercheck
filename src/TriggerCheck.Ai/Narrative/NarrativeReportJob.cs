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
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Ai.Narrative;

/// <summary>
/// AI-6 batch generation (never on a request or e-mail path). One run:
/// <list type="number">
/// <item>creates a <see cref="ReportNarrative"/> row (Requested) for the previous month of every eligible customer
/// (active, AI opted in, NarrativeReport in the plan) during the first <see cref="AiNarrativeOptions.AutoMonthlyDays"/>
/// days of the month in its report time zone; rows whose time zone or language no longer match are reset;</item>
/// <item>claims due rows (Requested, retryable Failed, abandoned Running) one by one with a conditional update, so
/// only one app instance ever works on a period (the unique index makes creation race-free too);</item>
/// <item>runs <see cref="IAiInterpreter"/> for each (opt-in, plan, budget, masking, contract and AiDecision recording
/// all happen there) and completes the row: Ready, Failed (retried up to <see cref="AiNarrativeOptions.MaxAttempts"/>)
/// or Skipped.</item>
/// </list>
/// Weekly narratives and older months are only generated when requested in the panel (<see cref="NarrativeReports.RequestAsync"/>).
/// </summary>
public sealed class NarrativeReportJob(AppDbContext db, IAiInterpreter interpreter, IAiClient client, IOptions<AiOptions> options,
    TimeProvider time, ILogger<NarrativeReportJob> logger)
{
    /// <returns>Rows completed in this run.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable || !o.Narrative.Enabled) return 0;
        await CreateMonthlyAsync(ct);
        return await ProcessAsync(ct);
    }

    private async Task CreateMonthlyAsync(CancellationToken ct)
    {
        var n = options.Value.Narrative;
        var now = time.GetUtcNow().UtcDateTime;
        var customers = (await db.Customers.AsNoTracking()
                .Where(c => c.IsActive && c.AiEnabled && c.Plan.AiMonthlyCallLimit > 0 && db.Monitors.Any(m => m.CustomerId == c.Id))
                .Select(c => new { c.Id, c.ReportTimeZoneId, c.AiLanguage, c.Plan.AiFeatures })
                .ToListAsync(ct))
            .Where(c => AiFeatures.Parse(c.AiFeatures).Contains(AiFeatures.NarrativeReport))
            .ToList();
        foreach (var c in customers)
        {
            var tz = SlaReportService.ResolveZone(c.ReportTimeZoneId);
            var local = MaintenanceSchedule.UtcToLocal(now, tz);
            if (local.Day > Math.Max(1, n.AutoMonthlyDays)) continue;
            var month = new DateTime(local.Year, local.Month, 1).AddMonths(-1);
            var period = NarrativePeriod.Month(month.Year, month.Month);
            await EnsureRequestedAsync(db, c.Id, period, ZoneId(c.ReportTimeZoneId), AiJob.NormalizeLanguage(c.AiLanguage), now, reset: false, ct);
        }
    }

    public static string ZoneId(string? reportTimeZoneId) =>
        SlaReportService.ResolveZone(reportTimeZoneId) == TimeZoneInfo.Utc ? SlaReportService.DefaultTimeZone : reportTimeZoneId!.Trim();

    /// <summary>
    /// Makes sure a row for the period exists and is not stale: a missing row is inserted as Requested (a parallel insert
    /// of another instance wins quietly); a finished row with another time zone or language, or any finished row when
    /// <paramref name="reset"/> is set (except Ready), goes back to Requested. Running rows are never touched.
    /// </summary>
    /// <returns>True: the period has a narrative row (new, reset, queued, running or ready); false: a finished row was kept.</returns>
    public static async Task<bool> EnsureRequestedAsync(AppDbContext db, int customerId, NarrativePeriod period, string timeZoneId, string language,
        DateTime now, bool reset, CancellationToken ct)
    {
        var row = await db.ReportNarratives.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CustomerId == customerId && r.Kind == period.Kind && r.PeriodKey == period.Key, ct);
        if (row is null)
        {
            db.ReportNarratives.Add(new ReportNarrative
            {
                CustomerId = customerId, Kind = period.Kind, PeriodKey = period.Key, TimeZoneId = timeZoneId, Language = language,
                Status = ReportNarrativeStatus.Requested, CreatedAtUtc = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // another instance created it
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
            return true;
        }
        var stale = row.TimeZoneId != timeZoneId || row.Language != language;
        var finished = row.Status is ReportNarrativeStatus.Ready or ReportNarrativeStatus.Failed or ReportNarrativeStatus.Skipped;
        if (finished && (stale || (reset && row.Status != ReportNarrativeStatus.Ready)))
        {
            var status = row.Status;
            await db.ReportNarratives.Where(r => r.Id == row.Id && r.Status == status)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, ReportNarrativeStatus.Requested)
                    .SetProperty(r => r.TimeZoneId, timeZoneId)
                    .SetProperty(r => r.Language, language)
                    .SetProperty(r => r.Attempts, 0)
                    .SetProperty(r => r.DecisionId, (long?)null)
                    .SetProperty(r => r.CompletedAtUtc, (DateTime?)null), ct);
            return true;
        }
        return row.Status is ReportNarrativeStatus.Requested or ReportNarrativeStatus.Running or ReportNarrativeStatus.Ready;
    }

    private async Task<int> ProcessAsync(CancellationToken ct)
    {
        var n = options.Value.Narrative;
        var now = time.GetUtcNow().UtcDateTime;
        var retryBefore = now.AddMinutes(-Math.Max(0, n.RetryMinutes));
        var abandonedBefore = now.AddMinutes(-Math.Max(1, n.RunningTimeoutMinutes));
        var maxAttempts = Math.Max(1, n.MaxAttempts);
        var due = await db.ReportNarratives.AsNoTracking()
            .Where(r => r.Status == ReportNarrativeStatus.Requested
                        || (r.Status == ReportNarrativeStatus.Failed && r.Attempts < maxAttempts && r.CompletedAtUtc < retryBefore)
                        || (r.Status == ReportNarrativeStatus.Running && r.StartedAtUtc < abandonedBefore))
            .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .Take(Math.Max(1, n.MaxPerRun))
            .ToListAsync(ct);

        var done = 0;
        foreach (var row in due)
        {
            // Claim: only one instance sees its update succeed (the row must still be in the state it was read in).
            var claimed = await db.ReportNarratives
                .Where(r => r.Id == row.Id && r.Status == row.Status && r.StartedAtUtc == row.StartedAtUtc && r.Attempts == row.Attempts)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, ReportNarrativeStatus.Running)
                    .SetProperty(r => r.StartedAtUtc, now)
                    .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
            if (claimed == 0) continue;

            ReportNarrativeStatus status;
            long? decisionId = null;
            try
            {
                var job = new AiJob(AiFeatures.NarrativeReport, row.CustomerId, NarrativePeriod.SubjectType, row.PeriodKey, row.Language);
                var decision = await interpreter.RunAsync(job, ct);
                decisionId = decision is { Id: > 0 } ? decision.Id : null;
                status = decision?.Status switch
                {
                    null => ReportNarrativeStatus.Skipped, // nothing to report (no monitors, period not complete, customer gone)
                    AiDecisionStatus.Succeeded => ReportNarrativeStatus.Ready,
                    AiDecisionStatus.Failed or AiDecisionStatus.Refused or AiDecisionStatus.Truncated or AiDecisionStatus.Pending => ReportNarrativeStatus.Failed,
                    _ => ReportNarrativeStatus.Skipped, // opt-out, plan, budget, AI off
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the Running claim is taken over after RunningTimeoutMinutes
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI narrative for customer {CustomerId} {Period} failed", row.CustomerId, row.PeriodKey);
                status = ReportNarrativeStatus.Failed;
            }

            var completed = time.GetUtcNow().UtcDateTime;
            await db.ReportNarratives.Where(r => r.Id == row.Id && r.Status == ReportNarrativeStatus.Running && r.StartedAtUtc == now)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.DecisionId, decisionId)
                    .SetProperty(r => r.CompletedAtUtc, completed), CancellationToken.None);
            done++;
        }
        return done;
    }
}

/// <summary>
/// Runs <see cref="NarrativeReportJob"/> every <see cref="AiNarrativeOptions.IntervalMinutes"/>. Does nothing when AI is
/// off, no key is configured or the job is disabled. Safe with several app instances (row claims).
/// </summary>
public sealed class NarrativeReportService(IServiceScopeFactory scopes, IAiClient client, IOptions<AiOptions> options, TimeProvider time,
    ILogger<NarrativeReportService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable || !o.Narrative.Enabled || o.Narrative.IntervalMinutes <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(o.Narrative.IntervalMinutes), time);
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<NarrativeReportJob>().RunAsync(stoppingToken);
                    if (n > 0) logger.LogInformation("Completed {Count} AI report narrative(s)", n);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "AI narrative job failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
