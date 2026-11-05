using System.Data;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Web.Heartbeat;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Support;

public enum MonitorCommandError
{
    None,
    /// <summary>The plan's monitor quota is used up.</summary>
    QuotaReached,
    /// <summary>The database write lock could not be obtained in time (SQLite busy).</summary>
    Busy,
    /// <summary>The monitor kept changing concurrently (or was deleted meanwhile).</summary>
    Conflict,
    NotFound
}

public sealed record MonitorCommandResult(MonitorEntity? Monitor, MonitorCommandError Error)
{
    public bool Succeeded => Error == MonitorCommandError.None;

    /// <summary>
    /// Heartbeat ping token issued by this command (create as / change to Heartbeat, or regeneration). Only its hash is
    /// stored, so the caller must show it now (panel: one-time reveal; API: <c>pingUrl</c> in this response).
    /// </summary>
    public string? IssuedHeartbeatToken { get; init; }
}

/// <summary>
/// Monitor create / update / delete shared by the customer panel and the REST API. Callers validate the
/// <see cref="MonitorInput"/> first (<see cref="MonitorInput.Validate(Plan, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary, Microsoft.Extensions.Localization.IStringLocalizer, string)"/>
/// plus the data annotations) and resolve the tenant themselves; every method is restricted to <c>customerId</c>.
/// Pause / resume live in <see cref="MonitorLifecycle"/>.
/// </summary>
public sealed class MonitorCommands(AppDbContext db, HeartbeatRateLimiter? heartbeatLimiter = null)
{
    public Task<int> CountAsync(int customerId, CancellationToken ct) =>
        db.Monitors.CountAsync(m => m.CustomerId == customerId, ct);

    /// <summary>Inserts a new monitor if the plan quota allows it (re-checked atomically).</summary>
    public async Task<MonitorCommandResult> CreateAsync(int customerId, Plan plan, MonitorInput input, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monitor = new MonitorEntity
        {
            CustomerId = customerId,
            IsEnabled = true,
            Status = MonitorStatus.Unknown,
            CreatedAtUtc = now,
            StatusChangedAtUtc = now,
            NextCheckAtUtc = now
        };
        input.ApplyTo(monitor);
        // A heartbeat is not probed: its first evaluation is when the first ping is due.
        if (monitor.Type == MonitorType.Heartbeat) monitor.NextCheckAtUtc = HeartbeatSchedule.FirstDeadline(monitor, now);

        // Re-count and insert atomically so parallel posts cannot exceed the plan quota. On SQLite a Serializable
        // transaction is BEGIN IMMEDIATE: it takes the write lock up front, so concurrent creates run one by one (on
        // Postgres the same through the transaction lock, see PostgresWriteLockInterceptor).
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!PlanRules.CanAddMonitor(plan, await CountAsync(customerId, ct)))
            {
                await tx.RollbackAsync(ct);
                return new(null, MonitorCommandError.QuotaReached);
            }
            db.Monitors.Add(monitor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (IsDatabaseBusy(ex))
        {
            // The write lock could not be obtained within the busy timeout (heavy load).
            db.ChangeTracker.Clear();
            return new(null, MonitorCommandError.Busy);
        }
        return new(monitor, MonitorCommandError.None) { IssuedHeartbeatToken = Known(input.IssuedHeartbeatToken) };
    }

    /// <summary>Loads a monitor of the customer for editing (tracked), or null.</summary>
    public Task<MonitorEntity?> FindTrackedAsync(int customerId, int id, CancellationToken ct) =>
        db.Monitors.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, ct);

    /// <summary>Applies <paramref name="input"/> to <paramref name="m"/> (loaded with <see cref="FindTrackedAsync"/>) and saves.</summary>
    public async Task<MonitorCommandResult> UpdateAsync(MonitorEntity m, MonitorInput input, CancellationToken ct)
    {
        // Bumping Version lets an in-flight check notice the edit (it discards results for changed probe settings).
        // If a check or a pause changed the row since we read it, the save fails on the token: reload the fresh row
        // and re-apply the form. Form fields are intentionally last-writer-wins between concurrent admins (no
        // "someone else edited this" check); runtime state written by checks or a pause is preserved.
        for (var attempt = 1; ; attempt++)
        {
            ApplyInput(m, input);
            try
            {
                await db.SaveChangesAsync(ct);
                // FEAT-6: heartbeat (push) monitors have no probe regions.
                if (m.Type == MonitorType.Heartbeat) await db.MonitorRegionStates.Where(s => s.MonitorId == m.Id).ExecuteDeleteAsync(ct);
                // ApplyTo issues a token on every attempt that switches to Heartbeat: the saved one is the last.
                return new(m, MonitorCommandError.None) { IssuedHeartbeatToken = Known(input.IssuedHeartbeatToken) };
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                await db.Entry(m).ReloadAsync(ct);
                if (db.Entry(m).State == EntityState.Detached) return new(null, MonitorCommandError.NotFound); // deleted meanwhile
            }
            catch (DbUpdateConcurrencyException)
            {
                return new(m, MonitorCommandError.Conflict);
            }
        }
    }

    /// <summary>
    /// Like <see cref="UpdateAsync"/> but saves once: when the row changed since it was loaded (Version token) it returns
    /// Conflict with the change tracker cleared instead of re-applying <paramref name="input"/> on fresh state. For callers
    /// that change one field and must re-read and re-validate the whole monitor on every attempt (AI-5 apply), so a
    /// concurrent edit is never overwritten with stale form values.
    /// </summary>
    public async Task<MonitorCommandResult> TryUpdateOnceAsync(MonitorEntity m, MonitorInput input, CancellationToken ct)
    {
        ApplyInput(m, input);
        try
        {
            await db.SaveChangesAsync(ct);
            if (m.Type == MonitorType.Heartbeat) await db.MonitorRegionStates.Where(s => s.MonitorId == m.Id).ExecuteDeleteAsync(ct);
            return new(m, MonitorCommandError.None) { IssuedHeartbeatToken = Known(input.IssuedHeartbeatToken) };
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(null, MonitorCommandError.Conflict);
        }
    }

    private static void ApplyInput(MonitorEntity m, MonitorInput input)
    {
        var before = ProbeSettings.Of(m);
        var intervalChanged = m.IntervalSeconds != input.IntervalSeconds;
        var becameHeartbeat = m.Type != MonitorType.Heartbeat && input.Type == MonitorType.Heartbeat;
        var thresholdBefore = m.FailureThreshold;
        input.ApplyTo(m);
        var probeChanged = ProbeSettings.Of(m) != before;
        // AI-5 judges only history recorded under the current settings (no ratchet after an apply or an edit).
        if (probeChanged || intervalChanged || m.FailureThreshold != thresholdBefore) m.SettingsChangedAtUtc = DateTime.UtcNow;

        if (probeChanged)
        {
            // Something else is being checked now: start over with a clean failure streak, and don't send an UP
            // alert for the new target just because the old one was down when the monitor was paused.
            m.ConsecutiveFailures = 0;
            m.DownWhenPaused = false;
        }
        if (m.Status != MonitorStatus.Paused && (probeChanged || intervalChanged))
            m.NextCheckAtUtc = m.Type == MonitorType.Heartbeat ? HeartbeatSchedule.AfterSettingsChange(m, becameHeartbeat, DateTime.UtcNow) : DateTime.UtcNow;
        m.Version++;
    }

    /// <summary>
    /// Heartbeat: replaces the ping token (the old URL stops working at once). NotFound for another tenant's monitor or
    /// a monitor that is not a heartbeat.
    /// </summary>
    public async Task<MonitorCommandResult> RegenerateHeartbeatTokenAsync(int customerId, int id, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var m = await FindTrackedAsync(customerId, id, ct);
            if (m is null || m.Type != MonitorType.Heartbeat) return new(null, MonitorCommandError.NotFound);
            var token = HeartbeatTokens.Issue(m);
            m.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
                return new(m, MonitorCommandError.None) { IssuedHeartbeatToken = Known(token) };
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                db.ChangeTracker.Clear(); // a ping or check changed the row: retry on fresh state
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return new(null, MonitorCommandError.Conflict);
            }
        }
    }

    /// <summary>Deletes the monitor with its history. Returns the deleted monitor's name, or null if not found.</summary>
    public async Task<string?> DeleteAsync(int customerId, int id, CancellationToken ct)
    {
        var name = await db.Monitors.AsNoTracking().Where(x => x.Id == id && x.CustomerId == customerId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        if (name is null) return null;

        // Set-based deletes: not subject to the Version concurrency token (a concurrent pause/check must not make
        // the delete fail). Notifications reference the monitor without an FK, so they are removed explicitly in the
        // same transaction; CheckResults and Incidents are removed by the database cascade.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Notifications.Where(n => n.MonitorId == id).ExecuteDeleteAsync(ct);
            await db.Monitors.Where(x => x.Id == id && x.CustomerId == customerId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        return name;
    }

    /// <summary>A newly issued ping token is known to the ping limiter right away (its first ping is never treated as a flood).</summary>
    private string? Known(string? token)
    {
        if (token is not null) heartbeatLimiter?.MarkKnown(HeartbeatTokens.Hash(token));
        return token;
    }

    /// <summary>The write lock could not be obtained in time (<see cref="DbErrors.IsBusy"/>).</summary>
    internal static bool IsDatabaseBusy(Exception ex) => DbErrors.IsBusy(ex);
}
