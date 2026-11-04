using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Maintenance;

namespace TriggerCheck.Monitoring.Regions;

/// <summary>
/// Serializes the application of remote results per monitor (singleton). Separate from <see cref="MonitorRunGate"/>,
/// which the central runner holds for the whole probe: results of agents must not wait for a slow central probe.
/// Central and remote writers still meet on the <see cref="Domain.Entities.Monitor.Version"/> token.
/// </summary>
public sealed class ProbeResultGate
{
    private readonly MonitorRunGate _gate = new();
    public Task<IDisposable> AcquireAsync(int monitorId, CancellationToken ct) => _gate.AcquireAsync(monitorId, ct);
}

/// <summary>
/// FEAT-6: the central side of the agent protocol (<see cref="AgentProtocol"/>), for an already authenticated region.
/// <list type="bullet">
/// <item><see cref="LeaseAsync"/>: due probes of that region only (monitor actively monitored, not heartbeat, plan allows
/// regions). Each gets a fresh random item id, an expiry (timeout + grace) and the fingerprint of its probe settings.
/// One outstanding lease per monitor and region; an expired lease is simply leased again with a new id.</item>
/// <item><see cref="IngestAsync"/>: a result is accepted only for the region's own current, unexpired item id
/// (foreign, superseded or unknown ids are rejected, a replay of an accepted id is a duplicate) and only if the monitor
/// still exists, is actively monitored and has the same probe settings. Accepted results are stored as CheckResults with
/// <see cref="CheckResult.RegionId"/> and voted on (<see cref="RegionQuorum"/>), with incidents and alerts exactly as the
/// central runner produces them.</item>
/// </list>
/// </summary>
public sealed class ProbeLeaseService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAlertQueue alerts,
    ProbeResultGate gate,
    TimeProvider time,
    IOptionsMonitor<RegionOptions> regionOptions,
    IOptionsMonitor<MonitoringOptions> monitoringOptions,
    ILogger<ProbeLeaseService> logger)
{
    private const int MaxSaveAttempts = 5;

    public async Task<LeaseResponse> LeaseAsync(int regionId, int maxItems, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var o = regionOptions.CurrentValue;
        maxItems = Math.Clamp(maxItems, 1, AgentProtocol.MaxLeaseItems);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.MonitorRegionStates.AsNoTracking()
            .Where(s => s.RegionId == regionId && s.Region.IsEnabled
                        && s.Monitor.IsEnabled && s.Monitor.Status != MonitorStatus.Paused && s.Monitor.Customer.IsActive
                        && s.Monitor.Type != MonitorType.Heartbeat && s.Monitor.Customer.Plan.MaxRegions != 1
                        && (s.NextCheckAtUtc == null || s.NextCheckAtUtc <= now)
                        && (s.LeasedUntilUtc == null || s.LeasedUntilUtc <= now))
            .OrderBy(s => s.NextCheckAtUtc)
            .Take(maxItems)
            .Select(s => s.Monitor)
            .ToListAsync(ct);

        var items = new List<LeaseItem>(due.Count);
        foreach (var monitor in due)
        {
            var itemId = NewItemId();
            var until = now.AddSeconds(Math.Clamp(monitor.TimeoutSeconds <= 0 ? 10 : monitor.TimeoutSeconds, 1, 120) + Math.Max(5, o.LeaseGraceSeconds));
            var fingerprint = RegionQuorum.Fingerprint(monitor);
            // Conditional: a concurrent lease call of the same region cannot hand out the same probe twice.
            var updated = await db.MonitorRegionStates
                .Where(s => s.MonitorId == monitor.Id && s.RegionId == regionId && (s.LeasedUntilUtc == null || s.LeasedUntilUtc <= now))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.LeaseItemId, itemId)
                    .SetProperty(s => s.LeasedAtUtc, now)
                    .SetProperty(s => s.LeasedUntilUtc, until)
                    .SetProperty(s => s.LeaseFingerprint, fingerprint), ct);
            if (updated == 1) items.Add(AgentProtocol.ToLeaseItem(monitor, itemId, until));
        }
        if (items.Count > 0) logger.LogDebug("Region {RegionId} leased {Count} probe(s)", regionId, items.Count);
        return new LeaseResponse { LeaseId = NewItemId(), ServerTimeUtc = now, Items = items };
    }

    private static string NewItemId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public async Task<IReadOnlyList<ResultAck>> IngestAsync(int regionId, IReadOnlyList<ProbeResult> results, CancellationToken ct)
    {
        var acks = new List<ResultAck>(results.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var itemId = r.ItemId ?? "";
            if (!seen.Add(itemId))
            {
                acks.Add(new ResultAck(itemId, ResultStatus.Duplicate));
                continue;
            }
            var status = await IngestOneAsync(regionId, r, ct);
            if (status is ResultStatus.Rejected or ResultStatus.Invalid)
                logger.LogWarning("Region {RegionId}: result for item {ItemId} {Status}", regionId, Cut(itemId, AgentProtocol.MaxItemIdLength), status);
            acks.Add(new ResultAck(itemId, status));
        }
        return acks;
    }

    private static string Cut(string s, int max) => RegionQuorum.Cut(s, max);

    private static bool IsValid(ProbeResult r) =>
        !string.IsNullOrEmpty(r.ItemId) && r.ItemId.Length <= AgentProtocol.MaxItemIdLength
        && r.ResponseTimeMs is >= 0 and <= 600_000
        && r.StatusCode is null or (>= 100 and <= 599)
        && r.DnsMs is null or (>= 0 and <= 600_000) && r.ConnectMs is null or (>= 0 and <= 600_000)
        && r.TlsMs is null or (>= 0 and <= 600_000) && r.TtfbMs is null or (>= 0 and <= 600_000);

    private async Task<ResultStatus> IngestOneAsync(int regionId, ProbeResult r, CancellationToken ct)
    {
        if (!IsValid(r)) return ResultStatus.Invalid;
        var now = time.GetUtcNow().UtcDateTime;
        int monitorId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Only this region's rows are searched: an item id of another region is simply unknown here.
            var row = await db.MonitorRegionStates.AsNoTracking()
                .Where(s => s.RegionId == regionId && (s.LeaseItemId == r.ItemId || s.LastResultItemId == r.ItemId))
                .Select(s => new { s.MonitorId, s.LeaseItemId, s.LeasedUntilUtc })
                .FirstOrDefaultAsync(ct);
            if (row is null) return ResultStatus.Rejected;
            if (row.LeaseItemId != r.ItemId) return ResultStatus.Duplicate;
            if (row.LeasedUntilUtc is not { } until || until < now)
            {
                await ClearLeaseAsync(db, row.MonitorId, regionId, r.ItemId, ct);
                return ResultStatus.Expired;
            }
            monitorId = row.MonitorId;
        }

        AlertMessage? alert = null;
        ResultStatus status;
        using (await gate.AcquireAsync(monitorId, ct))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    (status, alert) = await PersistAsync(regionId, monitorId, r, now);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
                {
                    logger.LogDebug("Monitor {MonitorId} changed while a region result was being saved; retrying", monitorId);
                }
                catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
                {
                    // Deleted between the read and the save (foreign key of the CheckResult).
                    await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
                    if (await db.Monitors.AnyAsync(m => m.Id == monitorId)) throw;
                    return ResultStatus.Discarded;
                }
            }
        }
        if (alert is not null) alerts.TryEnqueue(alert);
        return status;
    }

    private static Task<int> ClearLeaseAsync(AppDbContext db, int monitorId, int regionId, string itemId, CancellationToken ct) =>
        db.MonitorRegionStates.Where(s => s.MonitorId == monitorId && s.RegionId == regionId && s.LeaseItemId == itemId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LeaseItemId, (string?)null)
                .SetProperty(s => s.LeasedAtUtc, (DateTime?)null)
                .SetProperty(s => s.LeasedUntilUtc, (DateTime?)null)
                .SetProperty(s => s.LeaseFingerprint, (string?)null), ct);

    private async Task<(ResultStatus, AlertMessage?)> PersistAsync(int regionId, int monitorId, ProbeResult r, DateTime now)
    {
        var ct = CancellationToken.None; // once accepted, the state must be persisted
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var monitor = await db.Monitors.Include(m => m.Customer).SingleOrDefaultAsync(m => m.Id == monitorId, ct);
        if (monitor is null) return (ResultStatus.Discarded, null);
        var states = await RegionQuorum.LoadStatesAsync(db, monitorId, ct);
        var state = states.SingleOrDefault(s => s.RegionId == regionId);
        if (state is null) return (ResultStatus.Discarded, null); // region removed from the monitor meanwhile
        if (state.LeaseItemId != r.ItemId) return (state.LastResultItemId == r.ItemId ? ResultStatus.Duplicate : ResultStatus.Rejected, null);

        var leasedAt = state.LeasedAtUtc ?? now;
        var fingerprint = state.LeaseFingerprint;
        state.LeaseItemId = null;
        state.LeasedAtUtc = null;
        state.LeasedUntilUtc = null;
        state.LeaseFingerprint = null;
        if (fingerprint != RegionQuorum.Fingerprint(monitor) || !MonitorStateMachine.IsActivelyMonitored(monitor) || !RegionRules.SupportsRegions(monitor.Type))
        {
            // Edited, paused or disabled after the lease: the result no longer describes the monitor.
            await db.SaveChangesAsync(ct);
            return (ResultStatus.Discarded, null);
        }

        var o = regionOptions.CurrentValue;
        var checkedAt = PlausibleCheckTime(r, leasedAt, now, o);
        var error = r.IsUp ? null : Sanitize(r.Error) ?? "Check failed";
        var inMaintenance = await MaintenanceQueries.IsInMaintenanceAsync(db, monitor.CustomerId, monitor.Id, checkedAt, ct);
        db.CheckResults.Add(new CheckResult
        {
            MonitorId = monitor.Id, RegionId = regionId, CheckedAtUtc = checkedAt, IsUp = r.IsUp, StatusCode = r.StatusCode,
            ResponseTimeMs = r.ResponseTimeMs, Error = error, InMaintenance = inMaintenance,
            DnsMs = r.DnsMs, ConnectMs = r.ConnectMs, TlsMs = r.TlsMs, TtfbMs = r.TtfbMs
        });

        var wasFailing = state.ConsecutiveFailures > 0;
        state.LastCheckedAtUtc = checkedAt;
        state.LastIsUp = r.IsUp;
        state.LastError = error;
        state.LastResponseTimeMs = r.ResponseTimeMs;
        state.ConsecutiveFailures = r.IsUp ? 0 : state.ConsecutiveFailures + 1;
        state.LastResultItemId = r.ItemId;
        state.LastResultFingerprint = fingerprint;
        state.NextCheckAtUtc = MonitorStateMachine.NextCheckAfter(state.NextCheckAtUtc, monitor.IntervalSeconds, checkedAt);

        AlertMessage? alert = null;
        var votes = RegionQuorum.RemoteVotes(states, monitor, now, o);
        if (await RegionQuorum.CentralVoteAsync(db, monitor, now, o, ct) is { } central) votes.Add(central);
        var quorum = RegionQuorum.Tally(votes);
        if (quorum.UsesQuorum)
        {
            var transition = MonitorStateMachine.ApplyVote(monitor, quorum, checkedAt, inMaintenance);
            alert = inMaintenance
                ? await MonitorCheckRunner.RecoverInMaintenanceAsync(db, monitor, transition, checkedAt, logger, ct)
                : await MonitorCheckRunner.ApplyTransitionAsync(db, monitor, transition,
                    transition == StatusTransition.WentDown ? monitor.StatusChangedAtUtc ?? checkedAt : checkedAt, logger, ct, quorum);
            if (transition != StatusTransition.None) monitor.Version++;
        }
        // A new failure asks the other regions (and central) to confirm soon. Without a quorum this also lets central's
        // single-region rules see the outage quickly.
        if (!r.IsUp && !wasFailing && !inMaintenance && monitor.Status != MonitorStatus.Down)
        {
            var confirmAt = now.AddSeconds(Math.Clamp(monitoringOptions.CurrentValue.ConfirmationDelaySeconds, 0, 300));
            RegionQuorum.Expedite(states.Where(s => s.RegionId != regionId), confirmAt);
            if (monitor.NextCheckAtUtc is null || monitor.NextCheckAtUtc > confirmAt) monitor.NextCheckAtUtc = confirmAt;
        }

        await db.SaveChangesAsync(ct);
        return (ResultStatus.Accepted, await MonitorCheckRunner.WithIncidentAsync(db, alert, ct));
    }

    /// <summary>
    /// The agent's probe start, if it is plausible (inside the lease, allowing <see cref="RegionOptions.ClockSkewSeconds"/>);
    /// otherwise the server's estimate (now - response time). Keeps a skewed agent clock from backdating or postdating results.
    /// </summary>
    private static DateTime PlausibleCheckTime(ProbeResult r, DateTime leasedAtUtc, DateTime nowUtc, RegionOptions o)
    {
        var skew = TimeSpan.FromSeconds(Math.Max(0, o.ClockSkewSeconds));
        var reported = DateTime.SpecifyKind(r.CheckedAtUtc, DateTimeKind.Utc);
        if (reported >= leasedAtUtc - skew && reported <= nowUtc + skew) return reported > nowUtc ? nowUtc : reported;
        var estimate = nowUtc - TimeSpan.FromMilliseconds(r.ResponseTimeMs);
        return estimate < leasedAtUtc ? leasedAtUtc : estimate;
    }

    /// <summary>Agent-supplied error text: single line, no control characters, bounded.</summary>
    private static string? Sanitize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var s = new string(error.Take(AgentProtocol.MaxErrorLength).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? null : Checks.ProbeErrors.Truncate(s);
    }
}
