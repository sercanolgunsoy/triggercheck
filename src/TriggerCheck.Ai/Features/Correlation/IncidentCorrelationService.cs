using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Correlation;

namespace TriggerCheck.Ai.Features.Correlation;

/// <summary>What AI-4 does for a customer right now.</summary>
public enum CorrelationMode
{
    /// <summary>Not eligible (AI off globally, opted out, grouping switched off, not in the plan) or autonomy below A1.</summary>
    Off,
    /// <summary>A1: related incidents are proposed as groups; a CustomerAdmin accepts or rejects. Notifications unchanged.</summary>
    Suggest,
    /// <summary>A3: DOWN / UP notifications of rule-linked incidents are folded into the primary's notification.</summary>
    Automatic,
}

/// <param name="RuleMembersKept">Of the rule members of accepted proposals, how many the admin kept (rule precision).</param>
public sealed record CorrelationAcceptance(int Accepted, int Rejected, int RuleMembersKept, int RuleMembersTotal, int Days)
{
    public int Decided => Accepted + Rejected;
    /// <summary>Accepted / decided, null before the first decision. Decision K3: above 0.9 over four weeks → consider A3.</summary>
    public double? Rate => Decided == 0 ? null : (double)Accepted / Decided;
}

/// <summary>
/// AI-4 core: proposals (A1), the admin's decisions, automatic notification grouping (A3) and undoing it. Every method
/// is filtered by the customer id it is given (the session's RequiredCustomerId or the alert's owner), never by input
/// from the model. Incidents are only read, never opened, closed or changed.
/// <para>
/// Invariants of A3:
/// <list type="bullet">
/// <item>an incident is in at most one group (unique index); ambiguous links are never folded automatically;</item>
/// <item>a follower's DOWN is folded only when the group's primary DOWN was delivered, the customer is still in
/// <see cref="CorrelationMode.Automatic"/> (re-checked under the write lock) and the lock is free within
/// <see cref="AiCorrelationOptions.AlertPathLockTimeoutSeconds"/>; anything else sends the alert (fail open);</item>
/// <item>a folded DOWN is never lost: whenever the fold stops applying (group undone, mode no longer Automatic, fold
/// cap reached) and the incident is still open, the DOWN is queued on its own and only then marked
/// (<see cref="IncidentGroupMember.NotifiedAtUtc"/>); the watcher re-queues the unmarked ones (queue full / shut down,
/// crash, restart);</item>
/// <item>folded members still open are listed in one debounced "also affected" follow-up per group;</item>
/// <item>work that is not needed to decide an alert (undoing the group of a recovered primary) runs after the alert was
/// sent.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IncidentCorrelationService(
    AppDbContext db,
    IAiUsage usage,
    IOptions<AiOptions> aiOptions,
    IOptions<AiCorrelationOptions> options,
    CorrelationAutonomy autonomy,
    CorrelationRuntimeState runtime,
    IServiceProvider services,
    TimeProvider time,
    ILogger<IncidentCorrelationService> logger)
{
    /// <summary>Subject type of the AI decisions written for a group (subject id = group id).</summary>
    public const string SubjectType = "IncidentGroup";
    private const string DownSubjectPrefix = "[TriggerCheck] DOWN:";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Customer side of the gate (plan must be loaded): AI on globally, active, opted in, grouping on, in the plan.</summary>
    public static bool Eligible(Customer c, AiOptions o) =>
        o.Enabled && c.IsActive && c.AiEnabled && c.AlertGroupingEnabled && AiFeatures.PlanAllows(c.Plan, AiFeatures.CorrelationGrouping);

    /// <summary>
    /// The customer's mode. A used-up AI budget (AI-10) drops automatic grouping to proposals; the proposals' AI
    /// explanation is then skipped (recorded as SkippedBudget) while the free L1 rules keep working.
    /// </summary>
    public async Task<CorrelationMode> ModeAsync(Customer customer, CancellationToken ct = default)
    {
        if (!autonomy.Enabled || !Eligible(customer, aiOptions.Value)) return CorrelationMode.Off;
        if (!autonomy.Automatic) return CorrelationMode.Suggest;
        return (await usage.GetMonthlyUsageAsync(customer.Id, ct)).IsOverBudget ? CorrelationMode.Suggest : CorrelationMode.Automatic;
    }

    public Task<Customer?> CustomerAsync(int customerId, CancellationToken ct = default) =>
        db.Customers.AsNoTracking().Include(c => c.Plan).FirstOrDefaultAsync(c => c.Id == customerId, ct);

    private async Task<CorrelationMode> ModeAsync(int customerId, CancellationToken ct) =>
        await CustomerAsync(customerId, ct) is { } c ? await ModeAsync(c, ct) : CorrelationMode.Off;

    // ---------------------------------------------------------------------------------------------------------------
    // A1: proposals

    /// <summary>
    /// Proposes groups for the customer's recent incidents that are in no group yet: new incidents first join an open
    /// proposal whose primary started at most the window before them (rule link → member, ambiguous link → candidate),
    /// the rest is clustered by <see cref="CorrelationRules.Cluster"/>. Runs in one write transaction, so a proposal an
    /// admin decided meanwhile is never extended. Returns the ids of the proposals created or extended.
    /// </summary>
    public async Task<IReadOnlyList<int>> ProposeAsync(int customerId, CancellationToken ct = default)
    {
        var now = Now;
        var since = now.AddMinutes(-Math.Max(1, options.Value.LookbackMinutes));
        var window = options.Value.Window;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct); // SQLite: write lock first (serialized with Accept / Reject)
            var fresh = (await db.Incidents.AsNoTracking()
                    .Where(i => i.Monitor.CustomerId == customerId && i.StartedAtUtc >= since && !db.IncidentGroupMembers.Any(m => m.IncidentId == i.Id))
                    .Select(i => new { i.Id, i.MonitorId, i.Monitor.Type, i.Monitor.Target, i.Cause, i.StartedAtUtc })
                    .ToListAsync(ct))
                .Select(i => CorrelationRules.Describe(i.Id, i.MonitorId, i.Type, i.Target, i.Cause, i.StartedAtUtc))
                .OrderBy(i => i.StartedAtUtc).ThenBy(i => i.IncidentId)
                .ToList();
            if (fresh.Count == 0) return [];

            var changed = new HashSet<IncidentGroup>();
            var open = await db.IncidentGroups
                .Include(g => g.Members).ThenInclude(m => m.Incident).ThenInclude(i => i.Monitor)
                .Where(g => g.CustomerId == customerId && g.Status == IncidentGroupStatus.Proposed && g.CreatedAtUtc >= since)
                .OrderBy(g => g.Id)
                .ToListAsync(ct);
            foreach (var g in open)
            {
                var primary = g.Members.FirstOrDefault(m => m.Role == IncidentGroupRole.Primary);
                if (primary is null) continue;
                var core = g.Members.Where(m => m.Role != IncidentGroupRole.Ambiguous).Select(m => Describe(m.Incident)).ToList();
                var ambiguousCount = g.Members.Count(m => m.Role == IncidentGroupRole.Ambiguous);
                foreach (var f in fresh.Where(f => f.StartedAtUtc >= primary.Incident.StartedAtUtc && f.StartedAtUtc - primary.Incident.StartedAtUtc <= window).ToList())
                {
                    var strength = core.Select(c => CorrelationRules.Link(c, f, window).Strength).DefaultIfEmpty().Max();
                    if (strength == LinkStrength.None || (strength == LinkStrength.Ambiguous && ambiguousCount >= CorrelationRules.MaxAmbiguous)) continue;
                    var rule = strength == LinkStrength.Rule;
                    g.Members.Add(new IncidentGroupMember { IncidentId = f.IncidentId, CustomerId = customerId, Role = rule ? IncidentGroupRole.Rule : IncidentGroupRole.Ambiguous, Included = rule });
                    g.ExplanationQueuedAtUtc = null; // explain again with the new member
                    if (rule) core.Add(f);
                    else ambiguousCount++;
                    fresh.Remove(f);
                    changed.Add(g);
                }
            }

            var created = new List<IncidentGroup>();
            foreach (var c in CorrelationRules.Cluster(fresh, window))
            {
                var g = NewGroup(customerId, c, IncidentGroupStatus.Proposed, AiAutonomy.A1, now);
                g.Members.AddRange(c.Ambiguous.Select(a => new IncidentGroupMember { IncidentId = a.IncidentId, CustomerId = customerId, Role = IncidentGroupRole.Ambiguous }));
                db.IncidentGroups.Add(g);
                created.Add(g);
            }
            if (created.Count == 0 && changed.Count == 0) return [];
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return created.Concat(changed).Select(g => g.Id).Distinct().ToList();
        }
        catch (DbUpdateException ex)
        {
            // An incident was grouped concurrently (unique index): nothing is lost, the next scan starts over.
            logger.LogInformation(ex, "Incident grouping of customer {CustomerId} raced with another writer; retrying next scan", customerId);
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static IncidentGroup NewGroup(int customerId, CorrelationCluster c, IncidentGroupStatus status, AiAutonomy mode, DateTime now)
    {
        var g = new IncidentGroup
        {
            CustomerId = customerId, PrimaryIncidentId = c.Primary.IncidentId, Status = status, Mode = mode,
            Signals = string.Join(",", c.Signals), Host = c.Host, CreatedAtUtc = now,
        };
        g.Members.Add(new IncidentGroupMember { IncidentId = c.Primary.IncidentId, CustomerId = customerId, Role = IncidentGroupRole.Primary, Included = true });
        g.Members.AddRange(c.RuleMembers.Select(r => new IncidentGroupMember { IncidentId = r.IncidentId, CustomerId = customerId, Role = IncidentGroupRole.Rule, Included = true }));
        return g;
    }

    private static CorrelationIncident Describe(Incident i) =>
        CorrelationRules.Describe(i.Id, i.MonitorId, i.Monitor.Type, i.Monitor.Target, i.Cause, i.StartedAtUtc);

    /// <summary>Proposals nobody decided on in time become Expired (not counted in the acceptance rate).</summary>
    public Task<int> ExpireAsync(CancellationToken ct = default)
    {
        var cutoff = Now.AddHours(-Math.Max(1, options.Value.ProposalExpiryHours));
        return db.IncidentGroups.Where(g => g.Status == IncidentGroupStatus.Proposed && g.CreatedAtUtc < cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Expired), ct);
    }

    /// <summary>One group of the customer with its members, incidents and monitors (null: not found or another tenant's).</summary>
    public Task<IncidentGroup?> GetAsync(int customerId, int groupId, CancellationToken ct = default) =>
        db.IncidentGroups.AsNoTracking()
            .Include(g => g.Members).ThenInclude(m => m.Incident).ThenInclude(i => i.Monitor)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.CustomerId == customerId, ct);

    public enum DecisionResult { Done, NotFound, AlreadyDecided }

    /// <summary>
    /// A1 "accept": the admin keeps the members in <paramref name="keepIncidentIds"/> (rule members and ambiguous
    /// candidates alike; the primary always stays). Keeping none is a rejection. Decided once (serialized).
    /// </summary>
    public Task<DecisionResult> AcceptAsync(int customerId, int groupId, IReadOnlyCollection<int> keepIncidentIds, string? userId, CancellationToken ct = default) =>
        DecideAsync(customerId, groupId, userId, g =>
        {
            foreach (var m in g.Members.Where(m => m.Role != IncidentGroupRole.Primary)) m.Included = keepIncidentIds.Contains(m.IncidentId);
            g.Status = g.Members.Any(m => m.Role != IncidentGroupRole.Primary && m.Included) ? IncidentGroupStatus.Accepted : IncidentGroupStatus.Rejected;
        }, ct);

    public Task<DecisionResult> RejectAsync(int customerId, int groupId, string? userId, CancellationToken ct = default) =>
        DecideAsync(customerId, groupId, userId, g => g.Status = IncidentGroupStatus.Rejected, ct);

    private async Task<DecisionResult> DecideAsync(int customerId, int groupId, string? userId, Action<IncidentGroup> apply, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); // SQLite: write lock first, so two admins cannot both decide
        var g = await db.IncidentGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == groupId && x.CustomerId == customerId, ct);
        if (g is null) return DecisionResult.NotFound;
        if (g.Status != IncidentGroupStatus.Proposed) return DecisionResult.AlreadyDecided;
        apply(g);
        g.DecidedAtUtc = Now;
        g.DecidedByUserId = userId;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return DecisionResult.Done;
    }

    /// <summary>Acceptance rate of A1 proposals decided in the last <see cref="AiCorrelationOptions.AcceptanceWindowDays"/> (null customer = platform).</summary>
    public async Task<CorrelationAcceptance> AcceptanceAsync(int? customerId, CancellationToken ct = default)
    {
        var days = Math.Max(1, options.Value.AcceptanceWindowDays);
        var since = Now.AddDays(-days);
        var decided = db.IncidentGroups.AsNoTracking()
            .Where(g => g.Mode == AiAutonomy.A1 && g.DecidedAtUtc >= since && (customerId == null || g.CustomerId == customerId)
                        && (g.Status == IncidentGroupStatus.Accepted || g.Status == IncidentGroupStatus.Rejected));
        var counts = await decided.GroupBy(g => g.Status).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(ct);
        var rules = await db.IncidentGroupMembers.AsNoTracking()
            .Where(m => m.Role == IncidentGroupRole.Rule && m.Group.Mode == AiAutonomy.A1 && m.Group.Status == IncidentGroupStatus.Accepted
                        && m.Group.DecidedAtUtc >= since && (customerId == null || m.CustomerId == customerId))
            .GroupBy(m => m.Included).Select(x => new { x.Key, Count = x.Count() }).ToListAsync(ct);
        return new CorrelationAcceptance(
            counts.Where(c => c.Key == IncidentGroupStatus.Accepted).Sum(c => c.Count),
            counts.Where(c => c.Key == IncidentGroupStatus.Rejected).Sum(c => c.Count),
            rules.Where(r => r.Key).Sum(r => r.Count), rules.Sum(r => r.Count), days);
    }

    /// <summary>
    /// The customer switches grouping on or off. Off: in one write transaction (serialized with folds) the flag is
    /// cleared, open proposals expire and automatic groups are undone; then their still-open folded members are queued
    /// on their own (the watcher retries any that could not be queued).
    /// </summary>
    public async Task SetEnabledAsync(int customerId, bool enabled, string? userId, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is null) return;
            customer.AlertGroupingEnabled = enabled;
            await db.SaveChangesAsync(ct);
            if (!enabled)
            {
                await db.IncidentGroups.Where(g => g.CustomerId == customerId && g.Status == IncidentGroupStatus.Proposed)
                    .ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Expired), ct);
                await DissolveWhereAsync(db.IncidentGroups.Where(g => g.CustomerId == customerId && g.Status == IncidentGroupStatus.Auto), userId, ct);
            }
            await tx.CommitAsync(ct);
        }
        if (!enabled) await NotifyPendingAsync(customerId, null, ct);
    }

    private Task<int> DissolveWhereAsync(IQueryable<IncidentGroup> groups, string? userId, CancellationToken ct)
    {
        var now = Now;
        return groups.ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Dissolved)
            .SetProperty(g => g.DecidedAtUtc, now).SetProperty(g => g.DecidedByUserId, userId), ct);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A3: automatic notification grouping

    public static string FoldedDownReason(int primaryIncidentId) =>
        $"Grouped with incident #{primaryIncidentId} (AI-4): covered by its DOWN notification";

    public static string FoldedUpReason(int primaryIncidentId) =>
        $"Grouped with incident #{primaryIncidentId} (AI-4): recovered while the group's primary is still down";

    /// <summary>The UP path may skip the database: not automatic, and no automatic group is known to exist.</summary>
    private bool UpPathIdle => !autonomy.Automatic && !runtime.AutoGroupsMayExist;

    /// <summary>
    /// <see cref="IAlertGrouping"/> decision for one DOWN / UP alert. Null = deliver normally (also whenever anything
    /// is unclear). DOWN: folded only in <see cref="CorrelationMode.Automatic"/>, for an open incident that is in no
    /// group yet and is rule-linked (never ambiguous) to an earlier open incident whose own DOWN was delivered, started
    /// at most the window after the group's primary. UP: only a folded member that was never reported or notified on
    /// its own, while its primary is still down (read-only, no lock).
    /// </summary>
    public async Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct = default)
    {
        if (message.Kind == AlertKind.Up) return await FoldRecoveryAsync(message, ct);
        // A1 today: no database work at all on the alert path. A deferred DOWN is the held-back one: never fold again.
        if (message.Kind != AlertKind.Down || message.Deferred || !autonomy.Automatic) return null;
        if (services.GetService<IAlertQueue>() is null) return null; // could not send it later: never fold
        if (await ModeAsync(message.CustomerId, ct) != CorrelationMode.Automatic) return null;

        var cid = message.CustomerId;
        var incident = await db.Incidents.AsNoTracking().Include(i => i.Monitor)
            .Where(i => i.MonitorId == message.MonitorId && i.Monitor.CustomerId == cid && i.ResolvedAtUtc == null
                        && (message.IncidentId == null || i.Id == message.IncidentId))
            .OrderByDescending(i => i.StartedAtUtc).FirstOrDefaultAsync(ct);
        if (incident is null) return null;
        var me = Describe(incident);
        var window = options.Value.Window;
        var from = incident.StartedAtUtc - window;
        var earlier = (await db.Incidents.AsNoTracking().Include(i => i.Monitor)
                .Where(i => i.Monitor.CustomerId == cid && i.ResolvedAtUtc == null && i.Id != incident.Id && i.MonitorId != incident.MonitorId
                            && i.StartedAtUtc >= from && i.StartedAtUtc <= incident.StartedAtUtc)
                .ToListAsync(ct))
            .Where(i => i.StartedAtUtc < incident.StartedAtUtc || i.Id < incident.Id)
            .Select(i => (Incident: i, Link: CorrelationRules.Link(Describe(i), me, window)))
            .Where(x => x.Link.Strength == LinkStrength.Rule)
            .OrderBy(x => x.Incident.StartedAtUtc).ThenBy(x => x.Incident.Id)
            .ToList();
        if (earlier.Count == 0) return null;

        try
        {
            return await WithShortLockAsync(() => FoldLockedAsync(cid, incident, me, earlier, window, ct));
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            // Lock not free in time, unique index race, ...: the alert is sent on its own.
            logger.LogInformation(ex, "Automatic grouping of incident {IncidentId} skipped; alert sent on its own", incident.Id);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private async Task<string?> FoldLockedAsync(int cid, Incident incident, CorrelationIncident me, List<(Incident Incident, CorrelationLink Link)> earlier,
        TimeSpan window, CancellationToken ct)
    {
        // Serialized with switch-off, ungroup and undo (SQLite takes the write lock at BEGIN): eligibility and the group
        // state are re-read under the lock, so nothing is folded into a group that is being undone or after switch-off.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await ModeAsync(cid, ct) != CorrelationMode.Automatic) return null;
        if (await db.IncidentGroupMembers.AnyAsync(m => m.IncidentId == incident.Id, ct)) return null;
        var ids = earlier.Select(x => x.Incident.Id).ToList();
        var memberships = await db.IncidentGroupMembers.AsNoTracking().Include(m => m.Group)
            .Where(m => ids.Contains(m.IncidentId) && m.CustomerId == cid).ToListAsync(ct);
        foreach (var (candidate, link) in earlier)
        {
            var membership = memberships.FirstOrDefault(m => m.IncidentId == candidate.Id);
            int groupId, primaryId;
            if (membership is not null)
            {
                // Joins the automatic group the linked incident is in, if its primary is still open and early enough.
                if (membership.Group.Status != IncidentGroupStatus.Auto || !membership.Included) continue;
                primaryId = membership.Group.PrimaryIncidentId;
                var primaryStart = await db.Incidents.Where(i => i.Id == primaryId && i.ResolvedAtUtc == null && i.Monitor.CustomerId == cid)
                    .Select(i => (DateTime?)i.StartedAtUtc).FirstOrDefaultAsync(ct);
                if (primaryStart is null || incident.StartedAtUtc - primaryStart.Value > window) continue;
                groupId = membership.GroupId;
            }
            else
            {
                // A new group around the linked incident: only if it is still open (re-read under the write lock, so its
                // UP cannot slip past the group) and its own DOWN was delivered (otherwise nobody knows about the outage).
                if (!await db.Incidents.AnyAsync(i => i.Id == candidate.Id && i.ResolvedAtUtc == null, ct)) continue;
                if (!await DownDeliveredAsync(candidate, ct)) continue;
                var g = new IncidentGroup
                {
                    CustomerId = cid, PrimaryIncidentId = candidate.Id, Status = IncidentGroupStatus.Auto, Mode = AiAutonomy.A3,
                    Signals = string.Join(",", link.Signals), Host = me.Host, CreatedAtUtc = Now,
                };
                g.Members.Add(new IncidentGroupMember { IncidentId = candidate.Id, CustomerId = cid, Role = IncidentGroupRole.Primary, Included = true });
                db.IncidentGroups.Add(g);
                await db.SaveChangesAsync(ct);
                runtime.AutoGroupsMayExist = true;
                groupId = g.Id;
                primaryId = candidate.Id;
            }
            db.IncidentGroupMembers.Add(new IncidentGroupMember
            {
                GroupId = groupId, IncidentId = incident.Id, CustomerId = cid, Role = IncidentGroupRole.Rule, Included = true, FoldedAtUtc = Now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return FoldedDownReason(primaryId);
        }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a short SQLite busy timeout (<see cref="AiCorrelationOptions.AlertPathLockTimeoutSeconds"/>)
    /// for the write lock and its commands, so the alert path never waits the default 30 s; restores the timeouts after.
    /// </summary>
    private async Task<T> WithShortLockAsync<T>(Func<Task<T>> body)
    {
        var seconds = Math.Clamp(options.Value.AlertPathLockTimeoutSeconds, 1, 30);
        var connection = db.Database.GetDbConnection() as SqliteConnection;
        var previousConnection = connection?.DefaultTimeout;
        var previousCommand = db.Database.GetCommandTimeout();
        if (connection is not null) connection.DefaultTimeout = seconds;
        db.Database.SetCommandTimeout(seconds);
        try
        {
            return await body();
        }
        finally
        {
            if (connection is not null && previousConnection is int p) connection.DefaultTimeout = p;
            db.Database.SetCommandTimeout(previousCommand);
        }
    }

    /// <summary>The incident's DOWN alert reached at least one channel.</summary>
    private Task<bool> DownDeliveredAsync(Incident incident, CancellationToken ct) =>
        db.Notifications.AnyAsync(n => n.MonitorId == incident.MonitorId && n.Success && n.SentAtUtc >= incident.StartedAtUtc
                                       && n.Subject.StartsWith(DownSubjectPrefix), ct);

    /// <summary>The automatic group membership of the monitor's latest incident (null: none, or the path is idle).</summary>
    private async Task<IncidentGroupMember?> AutoMembershipAsync(AlertMessage message, CancellationToken ct)
    {
        if (UpPathIdle) return null;
        var cid = message.CustomerId;
        if (!await db.IncidentGroups.AnyAsync(g => g.CustomerId == cid && g.Status == IncidentGroupStatus.Auto, ct)) return null;
        var incidentId = await db.Incidents.Where(i => i.MonitorId == message.MonitorId && i.Monitor.CustomerId == cid)
            .OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        if (incidentId is null) return null;
        return await db.IncidentGroupMembers.AsNoTracking().Include(m => m.Group)
            .FirstOrDefaultAsync(m => m.IncidentId == incidentId && m.CustomerId == cid && m.Group.Status == IncidentGroupStatus.Auto, ct);
    }

    /// <summary>
    /// UP of a folded member while its primary is still down and nobody was told about the member yet (no follow-up,
    /// no own DOWN): the outage was covered by the primary's alert, so the recovery is folded too. Read-only.
    /// </summary>
    private async Task<string?> FoldRecoveryAsync(AlertMessage message, CancellationToken ct)
    {
        var member = await AutoMembershipAsync(message, ct);
        if (member is not { Role: not IncidentGroupRole.Primary, FoldedAtUtc: not null, ReportedAtUtc: null, NotifiedAtUtc: null }) return null;
        var primaryOpen = await db.Incidents.AnyAsync(i => i.Id == member.Group.PrimaryIncidentId && i.ResolvedAtUtc == null, ct);
        return primaryOpen ? FoldedUpReason(member.Group.PrimaryIncidentId) : null;
    }

    /// <summary>
    /// After an UP was sent: if it was a group's primary, the group is undone and its folded members still down are
    /// queued on their own. Uses the short lock timeout; on failure the watcher undoes the group (primary closed).
    /// </summary>
    public async Task AfterDeliveredAsync(AlertMessage message, CancellationToken ct = default)
    {
        if (message.Kind != AlertKind.Up) return;
        var member = await AutoMembershipAsync(message, ct);
        if (member is not { Role: IncidentGroupRole.Primary }) return;
        try
        {
            await WithShortLockAsync(() => DissolveAsync(message.CustomerId, member.GroupId, null, ct));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Undoing incident group {GroupId} deferred to the correlation watcher", member.GroupId);
        }
    }

    /// <summary>
    /// Undoes an automatic group (primary recovered, admin "ungroup", or the watcher): the group becomes Dissolved in a
    /// write transaction, then every folded member still down is queued on its own (<see cref="NotifyPendingAsync"/>).
    /// Returns the number of alerts queued.
    /// </summary>
    public async Task<int> DissolveAsync(int customerId, int groupId, string? userId, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var n = await DissolveWhereAsync(db.IncidentGroups.Where(g => g.Id == groupId && g.CustomerId == customerId && g.Status == IncidentGroupStatus.Auto), userId, ct);
            await tx.CommitAsync(ct);
            if (n == 0) return 0;
        }
        logger.LogInformation("Incident group {GroupId} of customer {CustomerId} undone", groupId, customerId);
        return await NotifyPendingAsync(customerId, groupId, ct);
    }

    /// <summary>
    /// Queues the own DOWN of every folded member whose fold no longer applies (group not automatic any more, or folded
    /// longer than <see cref="AiCorrelationOptions.MaxFoldMinutes"/>) and whose incident is still open, then marks it
    /// (<see cref="IncidentGroupMember.NotifiedAtUtc"/>). Marked only after a successful enqueue: a full or stopped queue
    /// leaves it for the next call (at least once). Optionally limited to one customer / group.
    /// </summary>
    public async Task<int> NotifyPendingAsync(int? customerId = null, int? groupId = null, CancellationToken ct = default)
    {
        var queue = services.GetService<IAlertQueue>();
        if (queue is null) return 0;
        var capCutoff = Now - options.Value.MaxFold;
        var pending = await db.IncidentGroupMembers.AsNoTracking().Include(m => m.Incident).ThenInclude(i => i.Monitor)
            .Where(m => m.FoldedAtUtc != null && m.NotifiedAtUtc == null && m.Incident.ResolvedAtUtc == null
                        && (m.Group.Status != IncidentGroupStatus.Auto || m.FoldedAtUtc <= capCutoff)
                        && (customerId == null || m.CustomerId == customerId) && (groupId == null || m.GroupId == groupId))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        var queued = 0;
        foreach (var m in pending)
        {
            var i = m.Incident;
            var alert = AlertMessage.For(i.Monitor, AlertKind.Down, i.StartedAtUtc) with { Error = i.Cause ?? i.Monitor.LastError, IncidentId = i.Id, Deferred = true };
            if (!queue.TryEnqueue(alert))
            {
                logger.LogWarning("Could not queue the held-back DOWN of incident {IncidentId}; retried by the correlation watcher", i.Id);
                continue;
            }
            await db.IncidentGroupMembers.Where(x => x.Id == m.Id && x.NotifiedAtUtc == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.NotifiedAtUtc, Now), CancellationToken.None);
            queued++;
        }
        if (queued > 0) logger.LogInformation("{Count} held-back DOWN alert(s) of grouped incidents queued on their own", queued);
        return queued;
    }

    /// <summary>
    /// One debounced "also affected" follow-up per automatic group: folded members still down that were neither reported
    /// nor notified on their own, once the earliest of them was folded at least
    /// <see cref="AiCorrelationOptions.FollowUpDelaySeconds"/> ago. Marked reported only after a successful enqueue.
    /// </summary>
    public async Task<int> SendFollowUpsAsync(CancellationToken ct = default)
    {
        var queue = services.GetService<IAlertQueue>();
        if (queue is null) return 0;
        var cutoff = Now - options.Value.FollowUpDelay;
        var pending = await db.IncidentGroupMembers.AsNoTracking().Include(m => m.Incident).ThenInclude(i => i.Monitor)
            .Where(m => m.Group.Status == IncidentGroupStatus.Auto && m.FoldedAtUtc != null && m.ReportedAtUtc == null && m.NotifiedAtUtc == null
                        && m.Incident.ResolvedAtUtc == null)
            .ToListAsync(ct);
        var sent = 0;
        foreach (var g in pending.GroupBy(m => m.GroupId).Where(g => g.Min(m => m.FoldedAtUtc) <= cutoff).OrderBy(g => g.Key))
        {
            var group = await db.IncidentGroups.AsNoTracking().FirstAsync(x => x.Id == g.Key, ct);
            var primary = await db.Incidents.AsNoTracking().Include(i => i.Monitor)
                .FirstOrDefaultAsync(i => i.Id == group.PrimaryIncidentId && i.Monitor.CustomerId == group.CustomerId && i.ResolvedAtUtc == null, ct);
            if (primary is null) continue; // the group is being undone: its members get their own DOWN instead
            var members = g.OrderBy(m => m.Incident.StartedAtUtc).ThenBy(m => m.Id).ToList();
            var alert = AlertMessage.ForGroupUpdate(primary.Monitor, primary.Id, members.Select(m => m.Incident.Monitor.Name).ToList(), Now);
            if (!queue.TryEnqueue(alert)) continue;
            var ids = members.Select(m => m.Id).ToList();
            var now = Now;
            await db.IncidentGroupMembers.Where(x => ids.Contains(x.Id) && x.ReportedAtUtc == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ReportedAtUtc, now), CancellationToken.None);
            sent++;
        }
        return sent;
    }

    /// <summary>
    /// Watcher step for A3, whatever the current mode: undoes automatic groups whose primary is closed or gone (closed
    /// without an UP alert, e.g. paused or deleted monitor) and those of customers no longer in
    /// <see cref="CorrelationMode.Automatic"/> (opt-out, Ai:Enabled off, autonomy lowered, plan or budget). Returns the
    /// number of groups undone; their members are queued by <see cref="NotifyPendingAsync"/>.
    /// </summary>
    public async Task<int> SettleAutomaticAsync(CancellationToken ct = default)
    {
        var auto = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.Status == IncidentGroupStatus.Auto)
            .Select(g => new { g.Id, g.CustomerId, PrimaryOpen = g.Members.Any(m => m.Role == IncidentGroupRole.Primary && m.Incident.ResolvedAtUtc == null) })
            .ToListAsync(ct);
        var settled = 0;
        foreach (var customer in auto.GroupBy(g => g.CustomerId))
        {
            var automatic = await ModeAsync(customer.Key, ct) == CorrelationMode.Automatic;
            foreach (var g in customer.Where(g => !automatic || !g.PrimaryOpen))
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                settled += await DissolveWhereAsync(db.IncidentGroups.Where(x => x.Id == g.Id && x.Status == IncidentGroupStatus.Auto), null, ct);
                await tx.CommitAsync(ct);
            }
        }
        return settled;
    }

    /// <summary>Automatic groups exist, or folded DOWNs are still to be sent on their own (the watcher's A3 work).</summary>
    public async Task<bool> HasAutomaticWorkAsync(CancellationToken ct = default) =>
        await db.IncidentGroups.AnyAsync(g => g.Status == IncidentGroupStatus.Auto, ct)
        || await db.IncidentGroupMembers.AnyAsync(m => m.FoldedAtUtc != null && m.NotifiedAtUtc == null && m.Incident.ResolvedAtUtc == null, ct);

    /// <summary>Refreshes the per-process hint used by the UP path (<see cref="CorrelationRuntimeState"/>).</summary>
    public async Task RefreshRuntimeStateAsync(CancellationToken ct = default) =>
        runtime.AutoGroupsMayExist = await db.IncidentGroups.AnyAsync(g => g.Status == IncidentGroupStatus.Auto, ct);
}
