using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Ai.Context;

/// <summary>
/// Read-only, tenant-scoped data access for building AI context. Every method takes the customer id from the job
/// (which comes from the session / the owning entity, never from model output) and filters by it, the same way the
/// customer panel filters by RequiredCustomerId: another tenant's id simply yields nothing.
/// </summary>
public sealed class AiTenantData(AppDbContext db)
{
    public Task<MonitorEntity?> MonitorAsync(int customerId, int monitorId, CancellationToken ct = default) =>
        db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == monitorId && m.CustomerId == customerId, ct);

    public Task<Incident?> IncidentAsync(int customerId, int incidentId, CancellationToken ct = default) =>
        db.Incidents.AsNoTracking().Include(i => i.Monitor)
            .FirstOrDefaultAsync(i => i.Id == incidentId && i.Monitor.CustomerId == customerId, ct);

    /// <summary>Newest first.</summary>
    public Task<List<CheckResult>> RecentChecksAsync(int customerId, int monitorId, int take, DateTime? beforeUtc = null, CancellationToken ct = default) =>
        db.CheckResults.AsNoTracking()
            .Where(r => r.MonitorId == monitorId && r.Monitor.CustomerId == customerId && (beforeUtc == null || r.CheckedAtUtc <= beforeUtc))
            .OrderByDescending(r => r.CheckedAtUtc)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>Other monitors of the same customer on the same host (for correlation hints).</summary>
    public async Task<List<MonitorEntity>> SameHostMonitorsAsync(int customerId, MonitorEntity monitor, CancellationToken ct = default)
    {
        var host = HostOf(monitor);
        if (host is null) return [];
        var candidates = await db.Monitors.AsNoTracking()
            .Where(m => m.CustomerId == customerId && m.Id != monitor.Id && m.Target.Contains(host))
            .ToListAsync(ct);
        return candidates.Where(m => string.Equals(HostOf(m), host, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public Task<List<AnomalySignal>> AnomaliesAsync(int customerId, int monitorId, DateTime sinceUtc, CancellationToken ct = default) =>
        db.AnomalySignals.AsNoTracking()
            .Where(a => a.CustomerId == customerId && a.MonitorId == monitorId && (a.EndedAtUtc == null || a.EndedAtUtc >= sinceUtc))
            .OrderByDescending(a => a.StartedAtUtc)
            .ToListAsync(ct);

    public Task<AnomalySignal?> AnomalyAsync(int customerId, long signalId, CancellationToken ct = default) =>
        db.AnomalySignals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == signalId && a.CustomerId == customerId, ct);

    /// <summary>The monitor's all-hours response time baseline (AI-1), null before the first baseline run.</summary>
    public Task<MonitorBaseline?> GlobalBaselineAsync(int customerId, int monitorId, CancellationToken ct = default) =>
        db.MonitorBaselines.AsNoTracking()
            .FirstOrDefaultAsync(b => b.CustomerId == customerId && b.MonitorId == monitorId && b.Bucket == MonitorBaseline.GlobalBucket, ct);

    /// <summary>Successful checks outside maintenance in [fromUtc, toUtc), newest first, at most <paramref name="take"/>.</summary>
    public Task<List<CheckResult>> SuccessfulChecksAsync(int customerId, int monitorId, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct = default) =>
        db.CheckResults.AsNoTracking()
            .Where(r => r.MonitorId == monitorId && r.Monitor.CustomerId == customerId && r.IsUp && !r.InMaintenance
                        && r.CheckedAtUtc >= fromUtc && r.CheckedAtUtc < toUtc)
            .OrderByDescending(r => r.CheckedAtUtc)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>Ids of the given monitors (of this customer) that have an open incident.</summary>
    public async Task<HashSet<int>> MonitorsWithOpenIncidentAsync(int customerId, IReadOnlyCollection<int> monitorIds, CancellationToken ct = default) =>
        (await db.Incidents.AsNoTracking()
            .Where(i => monitorIds.Contains(i.MonitorId) && i.Monitor.CustomerId == customerId && i.ResolvedAtUtc == null)
            .Select(i => i.MonitorId).Distinct().ToListAsync(ct)).ToHashSet();

    /// <summary>Maintenance periods of the monitor overlapping [fromUtc, toUtc) (customer-scoped, FEAT-3).</summary>
    public Task<List<MaintenanceOccurrence>> MaintenanceAsync(int customerId, int monitorId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        MaintenanceQueries.OccurrencesAsync(db, customerId, monitorId, fromUtc, toUtc, ct);

    public static string? HostOf(MonitorEntity m) =>
        m.Type is MonitorType.Http or MonitorType.Keyword
            ? Uri.TryCreate(m.Target, UriKind.Absolute, out var u) ? u.Host : null
            : string.IsNullOrWhiteSpace(m.Target) ? null : m.Target.Trim();
}
