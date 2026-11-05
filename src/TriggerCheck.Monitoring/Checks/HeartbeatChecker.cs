using System.Security.Cryptography;
using System.Text;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Monitoring.Checks;

/// <summary>
/// Secret ping tokens of heartbeat monitors. Like API keys: 256 random bits (base64url, 43 chars), only the SHA-256
/// hash and a short prefix are stored, so the full ping URL can be shown once (on creation / regeneration) and a
/// database leak does not reveal working URLs. An unsalted hash is fine for a uniformly random 256-bit secret.
/// </summary>
public static class HeartbeatTokens
{
    public const int TokenLength = 43;
    public const int PrefixLength = 8;

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Cheap shape check before any lookup (length + base64url alphabet).</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: TokenLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Display form of the ping URL path without the secret part.</summary>
    public static string MaskedPath(string? prefix) => "/hb/" + (prefix ?? "") + SecretMasker.Ellipsis;

    /// <summary>Gives <paramref name="monitor"/> a new token (replacing any previous one) and returns it.</summary>
    public static string Issue(MonitorEntity monitor)
    {
        var token = NewToken();
        monitor.HeartbeatTokenHash = Hash(token);
        monitor.HeartbeatTokenPrefix = token[..PrefixLength];
        monitor.Target = MaskedPath(monitor.HeartbeatTokenPrefix);
        return token;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Scheduling rules of heartbeat monitors. They are never probed: the check runner only evaluates the pings
/// received so far (see <see cref="HeartbeatMonitorChecker"/>), and only when there is something to decide:
/// <list type="bullet">
/// <item>a ping arrived: the ping endpoint sets NextCheckAtUtc = now, so the next scheduler tick evaluates it;</item>
/// <item>the deadline passed: NextCheckAtUtc = last ping (or the start of monitoring) + interval + grace.</item>
/// </list>
/// While overdue the monitor is re-evaluated once per interval, which records one failed check per missed interval.
/// </summary>
public static class HeartbeatSchedule
{
    public static TimeSpan Window(MonitorEntity m) =>
        TimeSpan.FromSeconds(Math.Max(1, m.IntervalSeconds)) + TimeSpan.FromSeconds(Math.Max(0, m.GracePeriodSeconds));

    /// <summary>First evaluation when monitoring (re)starts at <paramref name="nowUtc"/> (created, resumed, type changed).</summary>
    public static DateTime FirstDeadline(MonitorEntity m, DateTime nowUtc) => nowUtc + Window(m);

    /// <summary>
    /// NextCheckAtUtc after the user changed the settings of a heartbeat monitor: a monitor that just became a heartbeat
    /// (or never received a ping, or is still waiting for its first evaluation) gets a full window; otherwise it is
    /// re-evaluated right away with the new interval / grace.
    /// </summary>
    public static DateTime AfterSettingsChange(MonitorEntity m, bool becameHeartbeat, DateTime nowUtc) =>
        becameHeartbeat || m.LastHeartbeatAtUtc is null || m.Status == MonitorStatus.Unknown ? FirstDeadline(m, nowUtc) : nowUtc;

    /// <summary>Next evaluation after one at <paramref name="nowUtc"/>.</summary>
    public static DateTime NextEvaluation(MonitorEntity m, DateTime nowUtc)
    {
        if (m.LastHeartbeatAtUtc is { } last && last + Window(m) > nowUtc) return last + Window(m);
        return nowUtc + TimeSpan.FromSeconds(Math.Max(1, m.IntervalSeconds));
    }
}

/// <summary>
/// Evaluates a heartbeat (push) monitor without any outbound connection: UP when the last ping arrived within
/// interval + grace and did not report a failure. Confirmation does not apply to heartbeats: the grace period already
/// is the tolerance for a late ping, so an overdue evaluation is a confirmed failure (the runner skips the
/// confirmation probe and heartbeat monitors use a failure threshold of 1).
/// </summary>
public sealed class HeartbeatMonitorChecker(TimeProvider time) : IMonitorChecker
{
    public MonitorType Type => MonitorType.Heartbeat;

    public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct) =>
        Task.FromResult(Evaluate(monitor, time.GetUtcNow().UtcDateTime));

    public static CheckOutcome Evaluate(MonitorEntity monitor, DateTime nowUtc)
    {
        if (monitor.LastHeartbeatAtUtc is not { } last) return CheckOutcome.Down("No heartbeat received yet");
        if (monitor.LastHeartbeatFailed)
            return CheckOutcome.Down("Heartbeat reported a failure" + (string.IsNullOrEmpty(monitor.LastHeartbeatMessage) ? "" : ": " + monitor.LastHeartbeatMessage));
        var age = nowUtc - last;
        if (age >= HeartbeatSchedule.Window(monitor)) // the deadline itself is overdue: it is when the scheduler evaluates
            return CheckOutcome.Down($"No heartbeat for {Describe(age)} (expected every {Describe(TimeSpan.FromSeconds(monitor.IntervalSeconds))}" +
                                     $" + {Describe(TimeSpan.FromSeconds(monitor.GracePeriodSeconds))} grace)");
        return CheckOutcome.Up(0);
    }

    private static string Describe(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }
}
