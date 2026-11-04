using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.Billing;

public sealed record BillingMaintenanceResult(int Reminders, int TrialsExpired, int GraceExpired, int PeriodsEnded);

/// <summary>
/// Periodic billing work (idempotent: every step changes the state it selects on, so a re-run does nothing):
/// trial reminders, expired trials, past-due subscriptions after the grace period and cancelled subscriptions at
/// period end. The last three move the customer to the fallback plan.
/// </summary>
public sealed class BillingMaintenance(AppDbContext db, BillingService billing, BillingMailer mailer, TimeProvider time,
    IOptionsMonitor<BillingOptions> options, ILogger<BillingMaintenance> logger)
{
    public async Task<BillingMaintenanceResult> RunAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var fallback = await billing.FallbackPlanAsync(ct);
        if (fallback is null) { logger.LogError("Billing maintenance skipped: fallback plan {Plan} not found.", o.FallbackPlanName); return new(0, 0, 0, 0); }

        // 1. Reminder a few days before the trial ends (once).
        var remindFrom = now.AddDays(o.TrialReminderDaysBefore);
        var toRemind = await db.Customers.Include(c => c.Plan)
            .Where(c => c.BillingStatus == BillingStatus.Trial && c.TrialReminderSentAtUtc == null && c.TrialEndsAtUtc > now && c.TrialEndsAtUtc <= remindFrom)
            .ToListAsync(ct);
        foreach (var c in toRemind)
        {
            c.TrialReminderSentAtUtc = now;
            await db.SaveChangesAsync(ct);
            await mailer.TrialReminderAsync(c, fallback, ct);
        }

        // 2. Trials that ended without a subscription (a successful checkout sets the status to Active).
        var trialsExpired = 0;
        var expired = await db.Customers.Include(c => c.Plan)
            .Where(c => c.BillingStatus == BillingStatus.Trial && c.TrialEndsAtUtc <= now).ToListAsync(ct);
        foreach (var c in expired)
            if (await billing.DowngradeToFallbackAsync(c, null, mailer.TrialEndedReason, ct)) trialsExpired++;

        // 3. Past due beyond the grace period: stop renewals at the provider (best effort) and downgrade.
        var graceEnd = now.AddDays(-o.PastDueGraceDays);
        var graceExpired = 0;
        var pastDue = await db.Subscriptions.Include(s => s.Customer).ThenInclude(c => c.Plan)
            .Where(s => s.Status == SubscriptionStatus.PastDue && s.PastDueSinceUtc <= graceEnd).ToListAsync(ct);
        foreach (var s in pastDue)
        {
            if (s.ProviderReference is not null)
            {
                var r = await billing.CancelAtProviderAsync(s.ProviderReference, ct);
                if (!r) logger.LogWarning("Cancelling past-due subscription {SubscriptionId} at the provider failed; downgrading anyway.", s.Id);
            }
            if (await billing.DowngradeToFallbackAsync(s.Customer, s, mailer.GraceEndedReason, ct)) graceExpired++;
        }

        // 4. Cancelled subscriptions whose paid period is over.
        var periodsEnded = 0;
        var ended = await db.Subscriptions.Include(s => s.Customer).ThenInclude(c => c.Plan)
            .Where(s => s.Status == SubscriptionStatus.Cancelled && s.CurrentPeriodEndUtc <= now).ToListAsync(ct);
        foreach (var s in ended)
            if (await billing.DowngradeToFallbackAsync(s.Customer, s, mailer.PeriodEndedReason, ct)) periodsEnded++;

        var result = new BillingMaintenanceResult(toRemind.Count, trialsExpired, graceExpired, periodsEnded);
        if (result != new BillingMaintenanceResult(0, 0, 0, 0)) logger.LogInformation("Billing maintenance: {Result}", result);
        return result;
    }
}

/// <summary>Runs <see cref="BillingMaintenance"/> every <see cref="BillingOptions.MaintenanceIntervalMinutes"/> (0 = disabled).</summary>
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
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BillingMaintenance>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Billing maintenance failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
