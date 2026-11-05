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
/// Invariants: an incident is in at most one group (unique index); grouping never delays or blocks a DOWN alert (the
/// A3 decision is a few indexed queries, no model call, and fails open); a follower's DOWN is folded only when the
/// group's primary DOWN was actually delivered; when the primary recovers (or the group is undone / grouping switched
/// off) every still-open folded follower gets its own DOWN alert; ambiguous links are never folded automatically.
/// </para>
/// </summary>
public sealed class IncidentCorrelationService(
    AppDbContext db,
    IAiUsage usage,
    IOptions<AiOptions> aiOptions,
    IOptions<AiCorrelationOptions> options,
    CorrelationAutonomy autonomy,
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

    // ---------------------------------------------------------------------------------------------------------------
    // A1: proposals

    /// <summary>
    /// Proposes groups for the customer's recent incidents that are in no group yet: new incidents first join an open
    /// proposal whose primary started at most the window before them (rule link → member, ambiguous link → candidate),
    /// the rest is clustered by <see cref="CorrelationRules.Cluster"/>. Returns the ids of the proposals created or
    /// extended (their AI explanation is (re)queued by the caller).
    /// </summary>
    public async Task<IReadOnlyList<int>> ProposeAsync(int customerId, CancellationToken ct = default)
    {
        var now = Now;
        var since = now.AddMinutes(-Math.Max(1, options.Value.LookbackMinutes));
        var window = options.Value.Window;
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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // An incident was grouped concurrently (unique index): nothing is lost, the next scan starts over.
            logger.LogInformation(ex, "Incident grouping of customer {CustomerId} raced with another writer; retrying next scan", customerId);
            db.ChangeTracker.Clear();
            return [];
        }
        return created.Concat(changed).Select(g => g.Id).Distinct().ToList();
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
    /// The customer switches grouping on or off. Off: open proposals expire and automatic groups are undone (their
    /// still-open members get their own DOWN alert).
    /// </summary>
    public async Task SetEnabledAsync(int customerId, bool enabled, string? userId, CancellationToken ct = default)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null) return;
        customer.AlertGroupingEnabled = enabled;
        await db.SaveChangesAsync(ct);
        if (enabled) return;
        await db.IncidentGroups.Where(g => g.CustomerId == customerId && g.Status == IncidentGroupStatus.Proposed)
            .ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Expired), ct);
        var auto = await db.IncidentGroups.Where(g => g.CustomerId == customerId && g.Status == IncidentGroupStatus.Auto).Select(g => g.Id).ToListAsync(ct);
        foreach (var id in auto) await DissolveAsync(customerId, id, userId, ct);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A3: automatic notification grouping

    public static string FoldedDownReason(int primaryIncidentId) =>
        $"Grouped with incident #{primaryIncidentId} (AI-4): covered by its DOWN notification";

    public static string FoldedUpReason(int primaryIncidentId) =>
        $"Grouped with incident #{primaryIncidentId} (AI-4): recovered while the group's primary is still down";

    /// <summary>
    /// <see cref="IAlertGrouping"/> decision for one DOWN / UP alert. Null = deliver normally (also whenever anything
    /// is unclear). DOWN: folded only in <see cref="CorrelationMode.Automatic"/>, for an open incident that is in no
    /// group yet and is rule-linked (never ambiguous) to an earlier open incident whose own DOWN was delivered, started
    /// at most the window after the group's primary. UP: see <see cref="OnRecoveredAsync"/>.
    /// </summary>
    public async Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct = default)
    {
        if (message.Kind == AlertKind.Up) return await OnRecoveredAsync(message, ct);
        if (message.Kind != AlertKind.Down || !autonomy.Automatic) return null; // A1 today: no DB work on the alert path
        if (services.GetService<IAlertQueue>() is null) return null;            // could not undo it later: never fold
        var customer = await CustomerAsync(message.CustomerId, ct);
        if (customer is null || await ModeAsync(customer, ct) != CorrelationMode.Automatic) return null;

        var cid = customer.Id;
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
            // Serialized with DissolveAsync (SQLite takes the write lock at BEGIN), so a follower can never be folded
            // into a group that is being undone at the same moment.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
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
                    // A new group around the linked incident: only if it is still open (re-read under the write lock,
                    // so its UP cannot slip past the group) and its own DOWN was delivered (otherwise nobody knows).
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
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Automatic grouping of incident {IncidentId} raced with another writer; alert sent on its own", incident.Id);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    /// <summary>The incident's DOWN alert reached at least one channel.</summary>
    private Task<bool> DownDeliveredAsync(Incident incident, CancellationToken ct) =>
        db.Notifications.AnyAsync(n => n.MonitorId == incident.MonitorId && n.Success && n.SentAtUtc >= incident.StartedAtUtc
                                       && n.Subject.StartsWith(DownSubjectPrefix), ct);

    /// <summary>
    /// UP alert. Runs whatever the current mode, so groups made earlier are always undone correctly: the primary's
    /// recovery undoes its group (still-open folded members get their own DOWN now) and the UP is sent; a folded
    /// member's UP is folded too while its primary is still down.
    /// </summary>
    private async Task<string?> OnRecoveredAsync(AlertMessage message, CancellationToken ct)
    {
        var cid = message.CustomerId;
        if (!await db.IncidentGroups.AnyAsync(g => g.CustomerId == cid && g.Status == IncidentGroupStatus.Auto, ct)) return null;
        var incidentId = await db.Incidents.Where(i => i.MonitorId == message.MonitorId && i.Monitor.CustomerId == cid)
            .OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).Select(i => (int?)i.Id).FirstOrDefaultAsync(ct);
        if (incidentId is null) return null;
        var member = await db.IncidentGroupMembers.AsNoTracking().Include(m => m.Group)
            .FirstOrDefaultAsync(m => m.IncidentId == incidentId && m.CustomerId == cid && m.Group.Status == IncidentGroupStatus.Auto, ct);
        if (member is null) return null;
        if (member.Role != IncidentGroupRole.Primary)
        {
            if (member.FoldedAtUtc is null) return null;
            var primaryOpen = await db.Incidents.AnyAsync(i => i.Id == member.Group.PrimaryIncidentId && i.ResolvedAtUtc == null, ct);
            if (primaryOpen) return FoldedUpReason(member.Group.PrimaryIncidentId);
        }
        // The primary recovered (or closed without an UP alert): the others are on their own again.
        await DissolveAsync(cid, member.GroupId, null, ct);
        return null;
    }

    /// <summary>
    /// Undoes an automatic group (primary recovered, admin "ungroup", grouping switched off, or the primary closed
    /// without an UP alert): the group becomes Dissolved and every folded member whose incident is still open gets its
    /// own DOWN alert now. Returns the number of alerts queued.
    /// </summary>
    public async Task<int> DissolveAsync(int customerId, int groupId, string? userId, CancellationToken ct = default)
    {
        List<Incident> unfold;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var g = await db.IncidentGroups.Include(x => x.Members).ThenInclude(m => m.Incident).ThenInclude(i => i.Monitor)
                .FirstOrDefaultAsync(x => x.Id == groupId && x.CustomerId == customerId && x.Status == IncidentGroupStatus.Auto, ct);
            if (g is null) return 0;
            g.Status = IncidentGroupStatus.Dissolved;
            g.DecidedAtUtc = Now;
            g.DecidedByUserId = userId;
            unfold = g.Members.Where(m => m.FoldedAtUtc is not null && m.Incident.ResolvedAtUtc == null).Select(m => m.Incident).ToList();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        var queue = services.GetService<IAlertQueue>();
        var queued = 0;
        foreach (var i in unfold)
        {
            var alert = AlertMessage.For(i.Monitor, AlertKind.Down, i.StartedAtUtc) with { Error = i.Cause ?? i.Monitor.LastError, IncidentId = i.Id };
            if (queue?.TryEnqueue(alert) == true) queued++;
            else logger.LogError("Could not queue the DOWN alert of ungrouped incident {IncidentId} (monitor {MonitorId})", i.Id, i.MonitorId);
        }
        if (unfold.Count > 0)
            logger.LogInformation("Incident group {GroupId} of customer {CustomerId} undone; {Count} member(s) notified on their own", groupId, customerId, queued);
        return queued;
    }

    /// <summary>
    /// Safety net for A3 (watcher): automatic groups whose primary incident is closed or gone (closed without an UP
    /// alert, e.g. paused or deleted monitor) are undone. Runs whatever the mode.
    /// </summary>
    public async Task<int> SettleAutomaticAsync(CancellationToken ct = default)
    {
        var stale = await db.IncidentGroups.AsNoTracking()
            .Where(g => g.Status == IncidentGroupStatus.Auto
                        && !g.Members.Any(m => m.Role == IncidentGroupRole.Primary && m.Incident.ResolvedAtUtc == null))
            .Select(g => new { g.Id, g.CustomerId }).ToListAsync(ct);
        foreach (var g in stale) await DissolveAsync(g.CustomerId, g.Id, null, ct);
        return stale.Count;
    }
}
