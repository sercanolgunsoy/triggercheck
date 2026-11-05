using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Regions;

/// <summary>
/// Latest result of one region for one monitor. <see cref="Decisive"/>: a success, or a failure whose streak reached the
/// monitor's FailureThreshold; a failure below the threshold abstains (it can still confirm, see
/// <see cref="RegionQuorum.RemoteFailures"/>).
/// </summary>
public sealed record RegionVote(string RegionCode, bool IsUp, bool Decisive, DateTime CheckedAtUtc, DateTime FailingSinceUtc, string? Error);

/// <summary>Tally of the decisive votes of a monitor's regions (see <see cref="RegionQuorum"/>).</summary>
public sealed record QuorumDecision(int Active, int Up, int Down, IReadOnlyList<RegionVote> Failing, DateTime WindowStartUtc)
{
    /// <summary>
    /// The quorum decides only with at least <see cref="RegionQuorum.MinVoters"/> decisive votes and a strict majority;
    /// otherwise (two voters, a split, abstentions) central's single-region rules decide.
    /// </summary>
    public bool UsesQuorum => Active >= RegionQuorum.MinVoters && (UpMajority || DownMajority);
    public bool UpMajority => Up * 2 > Active;
    public bool DownMajority => Down * 2 > Active;

    /// <summary>Codes of the failing regions, e.g. "central,eu-central" (stored on the incident).</summary>
    public string FailedRegions => string.Join(",", Failing.Select(v => v.RegionCode));

    /// <summary>Outage start: the earliest start of the failing streaks, never before the vote window.</summary>
    public DateTime? FailingSinceUtc => Failing.Count == 0 ? null : Max(Failing.Min(v => v.FailingSinceUtc), WindowStartUtc);

    /// <summary>Customer-visible cause, e.g. "central: Connection refused; eu-central: Timeout after 10s".</summary>
    public string? Cause => Failing.Count == 0
        ? null
        : RegionQuorum.Cut(string.Join("; ", Failing.Select(v => $"{v.RegionCode}: {v.Error ?? "failed"}")), AgentProtocol.MaxErrorLength);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>
/// FEAT-6 voting rules (pure; the DB helpers only load the inputs).
/// <list type="bullet">
/// <item>Votes: central and every remote region within the plan's limit (<see cref="WithinPlan"/>) whose region is enabled,
/// whose agent is not stale, and whose last result is inside the vote window, not taken in maintenance and taken with the
/// monitor's current probe settings. A success votes UP; a failure votes DOWN once the region's streak reaches the
/// monitor's FailureThreshold (a single blip in one region is not a DOWN vote).</item>
/// <item>The quorum decides only with three or more decisive votes and a strict majority: DOWN when more than half fail,
/// UP when more than half succeed; a DOWN lasts at least <see cref="RegionOptions.MinTransitionSeconds"/> (hysteresis).</item>
/// <item>Otherwise - one or two voters, a split, abstentions, stale agents - central's single-region rules decide
/// (FailureThreshold, confirm before DOWN). Remote results then only serve as the confirmation of a central failure,
/// so a single lying or geo-blocked agent can neither take a monitor down nor keep it down or hide an outage.</item>
/// <item>A failure (while not DOWN) pulls the next probe of the regions and central forward to the confirmation delay,
/// at most once per interval (<see cref="TryExpedite"/>).</item>
/// </list>
/// Heartbeat monitors are never voted on.
/// </summary>
public static class RegionQuorum
{
    public const int MinVoters = 3;

    public static QuorumDecision Tally(IReadOnlyCollection<RegionVote> votes, DateTime windowStartUtc)
    {
        var decisive = votes.Where(v => v.Decisive).ToList();
        return new(decisive.Count, decisive.Count(v => v.IsUp), decisive.Count(v => !v.IsUp),
            decisive.Where(v => !v.IsUp).OrderBy(v => v.CheckedAtUtc).ToList(), windowStartUtc);
    }

    /// <summary>How old a region's last result may be and still vote.</summary>
    public static TimeSpan VoteWindow(MonitorEntity monitor, RegionOptions o) =>
        TimeSpan.FromSeconds(Math.Max(1, monitor.IntervalSeconds) * Math.Max(1, o.VoteWindowFactor)
                             + Math.Clamp(monitor.TimeoutSeconds, 1, 120) + Math.Max(0, o.LeaseGraceSeconds));

    public static bool IsAgentStale(Region region, DateTime nowUtc, RegionOptions o) =>
        region.LastSeenAtUtc is not { } seen || nowUtc - seen > TimeSpan.FromSeconds(Math.Max(1, o.AgentStaleAfterSeconds));

    private static int Threshold(MonitorEntity monitor) => Math.Max(1, monitor.FailureThreshold);

    /// <summary>
    /// The assignments the plan allows: the first <c>MaxRegions - 1</c> enabled ones by region id (all when MaxRegions is
    /// 0; a disabled region takes no slot). Others are neither leased nor counted, e.g. after a downgrade.
    /// </summary>
    public static List<MonitorRegionState> WithinPlan(IEnumerable<MonitorRegionState> states, int planMaxRegions) =>
        states.Where(s => s.Region.IsEnabled).OrderBy(s => s.RegionId).Take(planMaxRegions <= 0 ? int.MaxValue : Math.Max(0, planMaxRegions - 1)).ToList();

    /// <summary>
    /// The region's last result is eligible: region enabled, agent not stale, result inside the vote window, not taken in
    /// maintenance and taken with the monitor's current probe settings.
    /// </summary>
    public static bool IsEligible(MonitorRegionState s, MonitorEntity monitor, string fingerprint, DateTime nowUtc, RegionOptions o) =>
        s.Region.IsEnabled && !IsAgentStale(s.Region, nowUtc, o)
        && s.LastIsUp is not null && s.LastCheckedAtUtc is { } at && nowUtc - at <= VoteWindow(monitor, o)
        && !s.LastInMaintenance && s.LastResultFingerprint == fingerprint;

    /// <summary>Votes of the eligible remote regions in <paramref name="allowed"/> (Region loaded, already limited by the plan).</summary>
    public static List<RegionVote> RemoteVotes(IEnumerable<MonitorRegionState> allowed, MonitorEntity monitor, DateTime nowUtc, RegionOptions o)
    {
        var fingerprint = Fingerprint(monitor);
        return allowed
            .Where(s => IsEligible(s, monitor, fingerprint, nowUtc, o))
            .Select(s => new RegionVote(s.Region.Code, s.LastIsUp!.Value, s.LastIsUp!.Value || s.ConsecutiveFailures >= Threshold(monitor),
                s.LastCheckedAtUtc!.Value, s.FailingSinceUtc ?? s.LastCheckedAtUtc!.Value, s.LastError))
            .ToList();
    }

    /// <summary>Eligible remote failures (any streak): the confirmation signal for central's single-region rules.</summary>
    public static bool RemoteFailures(IEnumerable<MonitorRegionState> allowed, MonitorEntity monitor, DateTime nowUtc, RegionOptions o) =>
        RemoteVotes(allowed, monitor, nowUtc, o).Any(v => !v.IsUp);

    /// <summary>
    /// Central's vote for a probe taken at <paramref name="checkedAtUtc"/> with central streak <paramref name="streak"/>
    /// (failures including this one) that started at <paramref name="failingSinceUtc"/> (see
    /// <see cref="CentralFailingSinceAsync"/>). None in maintenance.
    /// </summary>
    public static RegionVote? CentralVote(MonitorEntity monitor, bool isUp, int streak, DateTime checkedAtUtc, DateTime? failingSinceUtc, string? error, bool inMaintenance)
    {
        if (inMaintenance) return null;
        var since = isUp || failingSinceUtc is not { } first || first > checkedAtUtc ? checkedAtUtc : first;
        return new RegionVote(RegionRules.CentralCode, isUp, isUp || streak >= Threshold(monitor), checkedAtUtc, since, error);
    }

    /// <summary>
    /// Probe time of the first central failure of the current streak: the oldest failing central result after the last
    /// successful (or in-maintenance, which starts no streak) one, looking back no further than <paramref name="windowStartUtc"/> (votes are clamped to the window
    /// anyway). Null when there is none in the window (the current, unsaved probe is then the first).
    /// </summary>
    public static async Task<DateTime?> CentralFailingSinceAsync(AppDbContext db, int monitorId, DateTime windowStartUtc, CancellationToken ct)
    {
        var central = db.CheckResults.AsNoTracking().Where(c => c.MonitorId == monitorId && c.RegionId == null && c.CheckedAtUtc >= windowStartUtc);
        var lastUp = await central.Where(c => c.IsUp || c.InMaintenance).MaxAsync(c => (DateTime?)c.CheckedAtUtc, ct) ?? windowStartUtc.AddTicks(-1);
        return await central.Where(c => !c.IsUp && c.CheckedAtUtc > lastUp).MinAsync(c => (DateTime?)c.CheckedAtUtc, ct);
    }

    /// <summary>Region states of a monitor with their regions (tracked, so callers can update them).</summary>
    public static Task<List<MonitorRegionState>> LoadStatesAsync(AppDbContext db, int monitorId, CancellationToken ct) =>
        db.MonitorRegionStates.Include(s => s.Region).Where(s => s.MonitorId == monitorId).ToListAsync(ct);

    public static Task<int> PlanMaxRegionsAsync(AppDbContext db, int customerId, CancellationToken ct) =>
        db.Customers.Where(c => c.Id == customerId).Select(c => c.Plan.MaxRegions).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The central scheduler's current vote, from its latest probe (if inside the vote window, not in maintenance and of
    /// the current probe settings) and its streak on the monitor.
    /// </summary>
    public static async Task<RegionVote?> CentralVoteAsync(AppDbContext db, MonitorEntity monitor, DateTime nowUtc, RegionOptions o, CancellationToken ct)
    {
        if (monitor.LastProbeFingerprint != Fingerprint(monitor)) return null;
        var since = nowUtc - VoteWindow(monitor, o);
        var last = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == monitor.Id && c.RegionId == null && c.CheckedAtUtc >= since)
            .OrderByDescending(c => c.CheckedAtUtc)
            .Select(c => new { c.IsUp, c.CheckedAtUtc, c.Error, c.InMaintenance })
            .FirstOrDefaultAsync(ct);
        if (last is null) return null;
        var failingSince = last.IsUp ? null : await CentralFailingSinceAsync(db, monitor.Id, since, ct);
        return CentralVote(monitor, last.IsUp, last.IsUp ? 0 : monitor.ConsecutiveFailures, last.CheckedAtUtc, failingSince, last.Error, last.InMaintenance);
    }

    /// <summary>
    /// "Confirmation from another region": a failure seen at <paramref name="failedAtUtc"/> pulls the next probe of the
    /// monitor's regions (the reporting one included: its re-check is its own confirmation) and of central forward to
    /// <paramref name="confirmAtUtc"/>. At most once per interval per monitor (bounded extra probe rate); regions that
    /// already reported after the failure or have a probe in flight are left alone. Returns whether it expedited.
    /// </summary>
    public static bool TryExpedite(MonitorEntity monitor, IEnumerable<MonitorRegionState> allowed, DateTime failedAtUtc, DateTime confirmAtUtc, DateTime nowUtc)
    {
        if (monitor.RegionsExpeditedAtUtc is { } last && nowUtc - last < TimeSpan.FromSeconds(Math.Max(1, monitor.IntervalSeconds))) return false;
        monitor.RegionsExpeditedAtUtc = nowUtc;
        foreach (var s in allowed)
        {
            if (!s.Region.IsEnabled || s.LastCheckedAtUtc > failedAtUtc || s.LeasedUntilUtc > nowUtc) continue;
            if (s.NextCheckAtUtc is null || s.NextCheckAtUtc > confirmAtUtc) s.NextCheckAtUtc = confirmAtUtc;
        }
        if (!(monitor.LastCheckedAtUtc > failedAtUtc) && (monitor.NextCheckAtUtc is null || monitor.NextCheckAtUtc > confirmAtUtc))
            monitor.NextCheckAtUtc = confirmAtUtc;
        return true;
    }

    internal static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Stable fingerprint of a monitor's probe settings (sent with a lease, compared when its result arrives).</summary>
    public static string Fingerprint(MonitorEntity monitor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProbeSettings.Of(monitor).ToString()))).ToLowerInvariant();
}
