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
using TriggerCheck.Monitoring.Cluster;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Ai.Narrative;

/// <summary>
/// AI-6 batch generation (never on a request or e-mail path). One run:
/// <list type="number">
/// <item>creates a <see cref="ReportNarrative"/> row (Requested) for the previous month of every eligible customer
/// (active, AI opted in, NarrativeReport in the plan) during the first <see cref="AiNarrativeOptions.AutoMonthlyDays"/>
/// days of the month in its report time zone; rows whose time zone or language no longer match are reset;</item>
/// <item>claims due rows (Requested, retryable Failed, abandoned Running with attempts left; monthly first) one by one
/// with a conditional update stamped with the claim's own time, so only one app instance ever works on a period (the
/// unique index makes creation race-free too);</item>
/// <item>runs <see cref="IAiInterpreter"/> for each (opt-in, plan, budget, masking, contract and AiDecision recording
/// all happen there) and completes the row: Ready, Failed (retried up to <see cref="AiNarrativeOptions.MaxAttempts"/>)
/// or Skipped.</item>
/// </list>
/// Weekly narratives and older months are only generated when requested in the panel (<see cref="NarrativeReports.RequestAsync"/>).
/// </summary>
public sealed class NarrativeReportJob(AppDbContext db, IAiInterpreter interpreter, IAiClient client, IOptions<AiOptions> options,
    TimeProvider time, ILogger<NarrativeReportJob> logger)
{
    /// <param name="renew">
    /// OPS-3 fencing: called before each claim; when it returns false (the cluster lease moved to another instance) the
    /// run stops. Null = no lease (tests, single instance).
    /// </param>
    /// <returns>Rows completed in this run.</returns>
    public async Task<int> RunAsync(CancellationToken ct = default, Func<CancellationToken, Task<bool>>? renew = null)
    {
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable || !o.Narrative.Enabled) return 0;
        await CreateMonthlyAsync(ct);
        return await ProcessAsync(renew, ct);
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
            await EnsureRequestedAsync(db, c.Id, period, ZoneId(c.ReportTimeZoneId), AiJob.NormalizeLanguage(c.AiLanguage), now, null, ct);
        }
    }

    public static string ZoneId(string? reportTimeZoneId) =>
        SlaReportService.ResolveZone(reportTimeZoneId) == TimeZoneInfo.Utc ? SlaReportService.DefaultTimeZone : reportTimeZoneId!.Trim();

    /// <summary>What <see cref="EnsureRequestedAsync"/> did.</summary>
    public enum EnsureResult
    {
        /// <summary>A row was created or reset to Requested.</summary>
        Queued,
        /// <summary>The row is already queued, running or ready (current zone and language): nothing to do.</summary>
        Present,
        /// <summary>A failed / skipped row was finished less than the request cool-down ago: kept as it is.</summary>
        CoolingDown,
    }

    /// <summary>
    /// Makes sure a row for the period exists and is not stale: a missing row is inserted as Requested (a parallel insert
    /// of another instance wins quietly); a finished row with another time zone or language goes back to Requested. With
    /// <paramref name="requestCooldown"/> (a panel request) a failed or skipped row is also requested again, but only
    /// once it finished at least that long ago, so repeated clicks neither reset the attempts nor record a new budget
    /// skip each time. Running and ready rows are never touched.
    /// </summary>
    public static async Task<EnsureResult> EnsureRequestedAsync(AppDbContext db, int customerId, NarrativePeriod period, string timeZoneId, string language,
        DateTime now, TimeSpan? requestCooldown, CancellationToken ct)
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
            return EnsureResult.Queued;
        }
        var stale = row.TimeZoneId != timeZoneId || row.Language != language;
        var finished = row.Status is ReportNarrativeStatus.Ready or ReportNarrativeStatus.Failed or ReportNarrativeStatus.Skipped;
        var retry = requestCooldown is not null && row.Status is ReportNarrativeStatus.Failed or ReportNarrativeStatus.Skipped;
        if (!finished || (!stale && !retry)) return EnsureResult.Present;
        if (!stale && row.CompletedAtUtc is DateTime done && done > now - requestCooldown!.Value) return EnsureResult.CoolingDown;

        var status = row.Status;
        await db.ReportNarratives.Where(r => r.Id == row.Id && r.Status == status)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ReportNarrativeStatus.Requested)
                .SetProperty(r => r.TimeZoneId, timeZoneId)
                .SetProperty(r => r.Language, language)
                .SetProperty(r => r.Attempts, 0)
                .SetProperty(r => r.DecisionId, (long?)null)
                .SetProperty(r => r.CompletedAtUtc, (DateTime?)null), ct);
        return EnsureResult.Queued;
    }

    private async Task<int> ProcessAsync(Func<CancellationToken, Task<bool>>? renew, CancellationToken ct)
    {
        var n = options.Value.Narrative;
        var runStart = time.GetUtcNow().UtcDateTime;
        var retryBefore = runStart.AddMinutes(-Math.Max(0, n.RetryMinutes));
        var abandonedBefore = runStart.AddMinutes(-Math.Max(1, n.RunningTimeoutMinutes));
        var maxAttempts = Math.Max(1, n.MaxAttempts);

        // A claim left Running by a crashed holder after its last allowed attempt is closed (never taken over again).
        await db.ReportNarratives
            .Where(r => r.Status == ReportNarrativeStatus.Running && r.StartedAtUtc < abandonedBefore && r.Attempts >= maxAttempts)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, ReportNarrativeStatus.Failed).SetProperty(r => r.CompletedAtUtc, runStart), ct);

        // Monthly narratives first (the e-mail waits for them), then requested weekly / older ones, oldest first.
        var due = await db.ReportNarratives.AsNoTracking()
            .Where(r => r.Status == ReportNarrativeStatus.Requested
                        || (r.Status == ReportNarrativeStatus.Failed && r.Attempts < maxAttempts && r.CompletedAtUtc < retryBefore)
                        || (r.Status == ReportNarrativeStatus.Running && r.Attempts < maxAttempts && r.StartedAtUtc < abandonedBefore))
            .OrderBy(r => r.Kind).ThenBy(r => r.CreatedAtUtc).ThenBy(r => r.Id)
            .Take(Math.Max(1, n.MaxPerRun))
            .ToListAsync(ct);

        var done = 0;
        foreach (var row in due)
        {
            if (renew is not null && !await renew(ct)) break; // the lease moved: another instance runs the job now
            // Claim with the time of THIS claim (not the run start): a slow run's later claims must not look abandoned
            // to another instance, which would take them over and pay for the call twice. Only one instance sees its
            // update succeed (the row must still be in the state it was read in).
            var claimedAt = time.GetUtcNow().UtcDateTime;
            var claimed = await db.ReportNarratives
                .Where(r => r.Id == row.Id && r.Status == row.Status && r.StartedAtUtc == row.StartedAtUtc && r.Attempts == row.Attempts
                            && r.Attempts < maxAttempts)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, ReportNarrativeStatus.Running)
                    .SetProperty(r => r.StartedAtUtc, claimedAt)
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
                throw; // the Running claim is taken over after RunningTimeoutMinutes (if attempts are left)
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI narrative for customer {CustomerId} {Period} failed", row.CustomerId, row.PeriodKey);
                status = ReportNarrativeStatus.Failed;
            }

            // Completes only our own claim (same claim time); a takeover in between keeps the newer holder's state.
            var completed = time.GetUtcNow().UtcDateTime;
            await db.ReportNarratives.Where(r => r.Id == row.Id && r.Status == ReportNarrativeStatus.Running && r.StartedAtUtc == claimedAt)
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
/// Runs <see cref="NarrativeReportJob"/> every <see cref="AiNarrativeOptions.IntervalMinutes"/> on one instance at a time
/// (OPS-3 cluster lease <see cref="LeaseName"/>; the row claims keep it correct even without it, the lease only stops
/// idle instances from polling). Does nothing when AI is off, no key is configured or the job is disabled.
/// </summary>
public sealed class NarrativeReportService(IServiceScopeFactory scopes, IAiClient client, IOptions<AiOptions> options, TimeProvider time,
    ILogger<NarrativeReportService> logger) : BackgroundService
{
    public const string LeaseName = "ai-narrative-report";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable || !o.Narrative.Enabled || o.Narrative.IntervalMinutes <= 0) return;
        var interval = TimeSpan.FromMinutes(o.Narrative.IntervalMinutes);
        // Renewed before every claim, so the lease only has to outlive one AI call.
        var lease = TimeSpan.FromMinutes(Math.Max(5, o.Narrative.RunningTimeoutMinutes));
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            using var timer = new PeriodicTimer(interval, time);
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var leases = scope.ServiceProvider.GetRequiredService<ClusterLeases>();
                    if (!await leases.TryAcquireAsync(LeaseName, lease, stoppingToken)) continue;
                    var n = await scope.ServiceProvider.GetRequiredService<NarrativeReportJob>()
                        .RunAsync(stoppingToken, ct => leases.RenewAsync(LeaseName, lease, ct));
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
        finally
        {
            await ClusterLeases.ReleaseOnStopAsync(scopes, LeaseName);
        }
    }
}
