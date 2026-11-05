using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Cluster;

namespace TriggerCheck.Web.Billing;

public sealed record BillingMaintenanceResult(int Reminders, int TrialsExpired, int GraceExpired, int PeriodsEnded,
    int CheckoutsResolved = 0, int PlanChangesApplied = 0, int UnconfirmedPurged = 0, int EventsReconciled = 0, int LateCheckoutsActivated = 0);

/// <summary>
/// Periodic billing work. Idempotent: every step changes the state it selects on (conditionally, so a webhook or a
/// second worker racing it wins cleanly), and one failing item is logged and skipped without stopping the run.
/// <list type="number">
/// <item>Re-check open checkouts with the provider (closed browser tab, lost callback); close them after the timeout.</item>
/// <item>Reconcile webhook events recorded for an unknown subscription, and re-ask failed checkouts (30 days, capped).</item>
/// <item>Trial reminder a few days before the end (once).</item>
/// <item>Trials that ended without a subscription: fallback plan.</item>
/// <item>Booked downgrades whose period is over.</item>
/// <item>Past-due subscriptions after the grace period: cancelled at the provider, fallback plan.</item>
/// <item>Cancelled subscriptions at the end of the paid period: fallback plan.</item>
/// <item>Sign-ups never confirmed within <see cref="BillingOptions.UnconfirmedAccountDays"/>: deleted.</item>
/// </list>
/// </summary>
public sealed class BillingMaintenance(AppDbContext db, BillingService billing, BillingMailer mailer, TimeProvider time,
    IOptionsMonitor<BillingOptions> options, ILogger<BillingMaintenance> logger)
{
    public async Task<BillingMaintenanceResult> RunAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;

        // 1. Open checkouts.
        var resolved = 0;
        var recheckBefore = now.AddMinutes(-o.CheckoutRecheckAfterMinutes);
        var timeoutBefore = now.AddMinutes(-o.CheckoutTimeoutMinutes);
        // Capped per run, least recently checked first, within a time budget.
        var open = await db.Subscriptions.AsNoTracking()
            .Where(s => (s.Status == SubscriptionStatus.Pending || s.Status == SubscriptionStatus.Superseded) && s.CheckoutToken != null && s.CreatedAtUtc <= recheckBefore)
            .OrderBy(s => s.LastCheckedAtUtc ?? DateTime.MinValue).ThenBy(s => s.Id)
            .Select(s => new { s.CheckoutToken, s.CreatedAtUtc }).Take(Math.Max(1, o.MaxCheckoutRechecksPerRun)).ToListAsync(ct);
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var c in open.TakeWhile(_ => !billing.OverBudget(budget)))
            await EachAsync("checkout re-check", async () =>
            {
                var (result, _) = await billing.CompleteCheckoutAsync(c.CheckoutToken!, ct, finalCheck: c.CreatedAtUtc <= timeoutBefore);
                if (result != CheckoutCompletion.Pending) resolved++;
            }, ct);

        // 1b. Unmatched webhook events and failed-but-maybe-paid checkouts (capped per run, throttled per item).
        var reconciled = 0;
        var late = 0;
        await EachAsync("event reconciliation", async () => reconciled = await billing.ReconcileIgnoredEventsAsync(20, ct), ct);
        await EachAsync("failed checkout re-scan", async () => late = await billing.RescanFailedCheckoutsAsync(20, ct), ct);

        var fallback = await billing.FallbackPlanAsync(ct);
        if (fallback is null) { logger.LogError("Billing maintenance skipped: fallback plan {Plan} not found.", o.FallbackPlanName); return new(0, 0, 0, 0, resolved); }

        // 2. Reminder a few days before the trial ends (once; claimed with a conditional update).
        var reminders = 0;
        var remindFrom = now.AddDays(o.TrialReminderDaysBefore);
        var toRemind = await db.Customers.AsNoTracking().Include(c => c.Plan)
            .Where(c => c.BillingStatus == BillingStatus.Trial && c.TrialReminderSentAtUtc == null && c.TrialEndsAtUtc > now && c.TrialEndsAtUtc <= remindFrom)
            .ToListAsync(ct);
        foreach (var c in toRemind)
            await EachAsync("trial reminder", async () =>
            {
                var claimed = await db.Customers.Where(x => x.Id == c.Id && x.TrialReminderSentAtUtc == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.TrialReminderSentAtUtc, now), ct);
                if (claimed == 0) return;
                try { await mailer.TrialReminderAsync(c, fallback, ct); }
                catch
                {
                    // Never "sent" without an outbox row: release the claim so the next run tries again.
                    await db.Customers.Where(x => x.Id == c.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.TrialReminderSentAtUtc, (DateTime?)null), CancellationToken.None);
                    throw;
                }
                reminders++;
            }, ct);

        // 3. Trials that ended without a subscription (a successful checkout sets the status to Active).
        var trialsExpired = 0;
        var expiredIds = await db.Customers.Where(c => c.BillingStatus == BillingStatus.Trial && c.TrialEndsAtUtc <= now).Select(c => c.Id).ToListAsync(ct);
        foreach (var id in expiredIds)
            await EachAsync("trial expiry", async () =>
            {
                var c = await db.Customers.Include(x => x.Plan).FirstAsync(x => x.Id == id, ct);
                if (await billing.DowngradeToFallbackAsync(c, null, BillingStatus.Trial, mailer.TrialEndedReason, ct)) trialsExpired++;
            }, ct);

        // 4. Booked downgrades whose period is over (normally applied by the renewal webhook already).
        var planChanges = 0;
        var scheduledIds = await db.Subscriptions.Where(s => s.Status == SubscriptionStatus.Active && s.ScheduledPlanId != null && s.CurrentPeriodEndUtc <= now)
            .Select(s => s.Id).ToListAsync(ct);
        foreach (var id in scheduledIds)
            await EachAsync("scheduled plan change", async () =>
            {
                var s = await db.Subscriptions.Include(x => x.Plan).FirstAsync(x => x.Id == id, ct);
                await billing.ApplyScheduledPlanAsync(s, ct);
                planChanges++;
            }, ct);

        // 5. Past due beyond the grace period: stop renewals at the provider (best effort) and downgrade.
        var graceEnd = now.AddDays(-o.PastDueGraceDays);
        var graceExpired = 0;
        var pastDueIds = await db.Subscriptions.Where(s => s.Status == SubscriptionStatus.PastDue && s.PastDueSinceUtc <= graceEnd).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in pastDueIds)
            await EachAsync("grace period", async () =>
            {
                var s = await db.Subscriptions.Include(x => x.Customer).ThenInclude(c => c.Plan).FirstAsync(x => x.Id == id, ct);
                if (s.ProviderReference is not null && !await billing.CancelAtProviderAsync(s.ProviderReference, ct))
                    logger.LogWarning("Cancelling past-due subscription {SubscriptionId} at the provider failed; downgrading anyway.", s.Id);
                if (await billing.DowngradeToFallbackAsync(s.Customer, s, BillingStatus.PastDue, mailer.GraceEndedReason, ct)) graceExpired++;
            }, ct);

        // 6. Cancelled subscriptions whose paid period is over.
        var periodsEnded = 0;
        var endedIds = await db.Subscriptions.Where(s => s.Status == SubscriptionStatus.Cancelled && s.CurrentPeriodEndUtc <= now).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in endedIds)
            await EachAsync("period end", async () =>
            {
                var s = await db.Subscriptions.Include(x => x.Customer).ThenInclude(c => c.Plan).FirstAsync(x => x.Id == id, ct);
                if (await billing.DowngradeToFallbackAsync(s.Customer, s, BillingStatus.Cancelled, mailer.PeriodEndedReason, ct)) periodsEnded++;
            }, ct);

        // 7. Self-service sign-ups still in their initial state whose address was never confirmed (no confirmed user, no
        // subscription, not touched by a SuperAdmin): the whole customer goes.
        var cutoff = now.AddDays(-o.UnconfirmedAccountDays);
        var purged = 0;
        await EachAsync("unconfirmed sign-up purge", async () =>
        {
            purged = await db.Customers
                .Where(c => c.SignupPending && c.CreatedAtUtc < cutoff && db.Users.Any(u => u.CustomerId == c.Id) && !db.Users.Any(u => u.CustomerId == c.Id && u.EmailConfirmed)
                            && !db.Subscriptions.Any(s => s.CustomerId == c.Id))
                .ExecuteDeleteAsync(ct);
        }, ct);

        var result = new BillingMaintenanceResult(reminders, trialsExpired, graceExpired, periodsEnded, resolved, planChanges, purged, reconciled, late);
        if (result != new BillingMaintenanceResult(0, 0, 0, 0)) logger.LogInformation("Billing maintenance: {Result}", result);
        return result;
    }

    /// <summary>Runs one item; a failure is logged and the run continues with the next item.</summary>
    private async Task EachAsync(string step, Func<Task> item, CancellationToken ct)
    {
        try { await item(); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Billing maintenance step '{Step}' failed for one item; continuing.", step);
        }
        finally { db.ChangeTracker.Clear(); }
    }
}

/// <summary>
/// Runs <see cref="BillingMaintenance"/> every <see cref="BillingOptions.MaintenanceIntervalMinutes"/> (0 = disabled).
/// OPS-3: only on the instance holding the <see cref="ClusterLeases.BillingMaintenance"/> lease; some steps (scheduled
/// plan changes, refused checkouts, event reconciliation) e-mail without a claim and would send twice if two
/// instances ran them concurrently.
/// </summary>
public sealed class BillingMaintenanceService(IServiceScopeFactory scopes, IOptionsMonitor<BillingOptions> options, TimeProvider time,
    ILogger<BillingMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.CurrentValue.MaintenanceIntervalMinutes;
        if (minutes <= 0) return;
        // E-mails of background work are sent in English (the default UI language).
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        // First run shortly after startup (not during it), then every interval.
        try { await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes), time);
        var lease = TimeSpan.FromMinutes(3 * minutes);
        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<ClusterLeases>().TryAcquireAsync(ClusterLeases.BillingMaintenance, lease, stoppingToken))
                        await scope.ServiceProvider.GetRequiredService<BillingMaintenance>().RunAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Billing maintenance failed.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            await ClusterLeases.ReleaseOnStopAsync(scopes, ClusterLeases.BillingMaintenance);
        }
    }
}
