using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Engine;
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
}

/// <summary>
/// Monitor create / update / delete shared by the customer panel and the REST API. Callers validate the
/// <see cref="MonitorInput"/> first (<see cref="MonitorInput.Validate(Plan, Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary, Microsoft.Extensions.Localization.IStringLocalizer, string)"/>
/// plus the data annotations) and resolve the tenant themselves; every method is restricted to <c>customerId</c>.
/// Pause / resume live in <see cref="MonitorLifecycle"/>.
/// </summary>
public sealed class MonitorCommands(AppDbContext db)
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

        // Re-count and insert atomically so parallel posts cannot exceed the plan quota. On SQLite a Serializable
        // transaction is BEGIN IMMEDIATE: it takes the write lock up front, so concurrent creates run one by one.
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
        return new(monitor, MonitorCommandError.None);
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
                return new(m, MonitorCommandError.None);
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

    private static void ApplyInput(MonitorEntity m, MonitorInput input)
    {
        var before = ProbeSettings.Of(m);
        var intervalChanged = m.IntervalSeconds != input.IntervalSeconds;
        input.ApplyTo(m);
        var probeChanged = ProbeSettings.Of(m) != before;

        if (probeChanged)
        {
            // Something else is being checked now: start over with a clean failure streak, and don't send an UP
            // alert for the new target just because the old one was down when the monitor was paused.
            m.ConsecutiveFailures = 0;
            m.DownWhenPaused = false;
        }
        if (m.Status != MonitorStatus.Paused && (probeChanged || intervalChanged)) m.NextCheckAtUtc = DateTime.UtcNow;
        m.Version++;
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

    /// <summary>SQLITE_BUSY (5) / SQLITE_LOCKED (6), possibly wrapped in a DbUpdateException.</summary>
    internal static bool IsDatabaseBusy(Exception ex) =>
        (ex as SqliteException ?? ex.InnerException as SqliteException) is { SqliteErrorCode: 5 or 6 };
}
