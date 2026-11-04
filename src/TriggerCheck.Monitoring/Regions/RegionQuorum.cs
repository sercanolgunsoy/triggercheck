using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Regions;

/// <summary>Latest result of one region for one monitor, as it counts in the quorum.</summary>
public sealed record RegionVote(string RegionCode, bool IsUp, DateTime CheckedAtUtc, string? Error);

/// <summary>
/// Tally of the votes of a monitor's active regions. Active = central plus every enabled remote region whose agent is
/// not stale and whose last result is inside the vote window (see <see cref="RegionQuorum"/>).
/// </summary>
public sealed record QuorumDecision(int Active, int Up, int Down, IReadOnlyList<RegionVote> Failing)
{
    /// <summary>Quorum voting applies with two or more active regions; with one the classic single-region rules apply.</summary>
    public bool UsesQuorum => Active >= 2;
    public bool UpMajority => Up * 2 > Active;
    public bool DownMajority => Down * 2 > Active;

    /// <summary>Codes of the failing regions, e.g. "central,eu-central" (stored on the incident).</summary>
    public string FailedRegions => string.Join(",", Failing.Select(v => v.RegionCode));

    /// <summary>Outage start: the earliest of the failing votes.</summary>
    public DateTime? FailingSinceUtc => Failing.Count == 0 ? null : Failing.Min(v => v.CheckedAtUtc);

    /// <summary>Customer-visible cause, e.g. "central: Connection refused; eu-central: Timeout after 10s".</summary>
    public string? Cause => Failing.Count == 0
        ? null
        : RegionQuorum.Cut(string.Join("; ", Failing.Select(v => $"{v.RegionCode}: {v.Error ?? "failed"}")), AgentProtocol.MaxErrorLength);
}

/// <summary>
/// FEAT-6 voting rules (pure; the DB helpers only load the inputs). A monitor's status is decided by the majority of its
/// active regions: DOWN only when more than half report a failure, UP when more than half report success; otherwise the
/// status is kept. The agreement of a second region replaces the confirmation probe and FailureThreshold of the
/// single-region rules (ENG-9): the confirmation comes from another region. When fewer than two regions are active
/// (stale agents, disabled regions, no fresh results) the monitor falls back to the single-region rules, so it is never
/// left unmonitored. Heartbeat monitors are never voted on.
/// </summary>
public static class RegionQuorum
{
    public static QuorumDecision Tally(IReadOnlyCollection<RegionVote> votes) =>
        new(votes.Count, votes.Count(v => v.IsUp), votes.Count(v => !v.IsUp), votes.Where(v => !v.IsUp).OrderBy(v => v.CheckedAtUtc).ToList());

    /// <summary>How old a region's last result may be and still vote.</summary>
    public static TimeSpan VoteWindow(MonitorEntity monitor, RegionOptions o) =>
        TimeSpan.FromSeconds(Math.Max(1, monitor.IntervalSeconds) * Math.Max(1, o.VoteWindowFactor)
                             + Math.Clamp(monitor.TimeoutSeconds, 1, 120) + Math.Max(0, o.LeaseGraceSeconds));

    public static bool IsAgentStale(Region region, DateTime nowUtc, RegionOptions o) =>
        region.LastSeenAtUtc is not { } seen || nowUtc - seen > TimeSpan.FromSeconds(Math.Max(1, o.AgentStaleAfterSeconds));

    /// <summary>Votes of the remote regions in <paramref name="states"/> (Region loaded) that currently count.</summary>
    public static List<RegionVote> RemoteVotes(IEnumerable<MonitorRegionState> states, MonitorEntity monitor, DateTime nowUtc, RegionOptions o)
    {
        var fingerprint = Fingerprint(monitor);
        return states
            .Where(s => IsVoting(s, monitor, fingerprint, nowUtc, o))
            .Select(s => new RegionVote(s.Region.Code, s.LastIsUp!.Value, s.LastCheckedAtUtc!.Value, s.LastError))
            .ToList();
    }

    /// <summary>
    /// The region's last result counts: region enabled, agent not stale, result inside the vote window and taken with the
    /// monitor's current probe settings.
    /// </summary>
    public static bool IsVoting(MonitorRegionState s, MonitorEntity monitor, string fingerprint, DateTime nowUtc, RegionOptions o) =>
        s.Region.IsEnabled && !IsAgentStale(s.Region, nowUtc, o)
        && s.LastIsUp is not null && s.LastCheckedAtUtc is { } at && nowUtc - at <= VoteWindow(monitor, o)
        && s.LastResultFingerprint == fingerprint;

    /// <summary>Region states of a monitor with their regions (tracked, so callers can update them).</summary>
    public static Task<List<MonitorRegionState>> LoadStatesAsync(AppDbContext db, int monitorId, CancellationToken ct) =>
        db.MonitorRegionStates.Include(s => s.Region).Where(s => s.MonitorId == monitorId).ToListAsync(ct);

    /// <summary>The central scheduler's latest probe of the monitor, if inside the vote window.</summary>
    public static async Task<RegionVote?> CentralVoteAsync(AppDbContext db, MonitorEntity monitor, DateTime nowUtc, RegionOptions o, CancellationToken ct)
    {
        var since = nowUtc - VoteWindow(monitor, o);
        var last = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == monitor.Id && c.RegionId == null && c.CheckedAtUtc >= since)
            .OrderByDescending(c => c.CheckedAtUtc)
            .Select(c => new { c.IsUp, c.CheckedAtUtc, c.Error })
            .FirstOrDefaultAsync(ct);
        return last is null ? null : new RegionVote(RegionRules.CentralCode, last.IsUp, last.CheckedAtUtc, last.Error);
    }

    /// <summary>
    /// "Confirmation from another region": a region that starts failing pulls the next probe of the monitor's other
    /// regions forward to <paramref name="confirmAtUtc"/>, so a real outage is confirmed within seconds instead of an
    /// interval. Only the first failure of a streak expedites (a region failing alone does not multiply the check rate).
    /// </summary>
    public static void Expedite(IEnumerable<MonitorRegionState> otherRegions, DateTime confirmAtUtc)
    {
        foreach (var s in otherRegions)
            if (s.Region.IsEnabled && (s.NextCheckAtUtc is null || s.NextCheckAtUtc > confirmAtUtc))
                s.NextCheckAtUtc = confirmAtUtc;
    }

    internal static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Stable fingerprint of a monitor's probe settings (sent with a lease, compared when its result arrives).</summary>
    public static string Fingerprint(MonitorEntity monitor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProbeSettings.Of(monitor).ToString()))).ToLowerInvariant();
}
