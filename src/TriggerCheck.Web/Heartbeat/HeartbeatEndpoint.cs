using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Heartbeat;

/// <summary>Settings of the heartbeat ping endpoint (section <c>Heartbeat</c>).</summary>
public sealed class HeartbeatOptions
{
    public const string Section = "Heartbeat";
    /// <summary>Pings accepted per token and minute (a job pings once per interval; a few retries are fine).</summary>
    public int PingsPerMinutePerToken { get; set; } = 10;
    /// <summary>
    /// Pings per client (IPv4 address / IPv6 /64) and minute. High on purpose: a whole fleet behind one NAT pings from a
    /// single address and must never be throttled into a false DOWN. Behind a reverse proxy, enable ForwardedHeaders,
    /// otherwise every client shares the proxy's address.
    /// </summary>
    public int PingsPerMinutePerClient { get; set; } = 6000;
    /// <summary>Global flood guard over all clients and tokens, per second.</summary>
    public int PingsPerSecondGlobal { get; set; } = 1000;
}

/// <summary>
/// Ping endpoint of heartbeat (push) monitors: <c>GET|POST|HEAD /hb/{token}</c>, optionally <c>?status=fail</c> (the
/// job reports a failure: counts as a failed check right away) and <c>&amp;msg=...</c> (short message, truncated and
/// masked; nothing else of the request, and never the body, is stored).
/// <list type="bullet">
/// <item>Anonymous (AllowAnonymous: no SessionValidator lookup, no cookies needed, so there is nothing to forge
/// cross-site; the secret is the token itself).</item>
/// <item>No existence oracle: known, unknown and malformed tokens get the same 200 response and body. Only the
/// rate limit (<see cref="HeartbeatRateLimiter"/>, independent of whether the token exists) answers 429.</item>
/// <item>Pings of paused monitors or of suspended customers are ignored (resume / reactivation start a new window).</item>
/// <item>A ping records the time / failure flag on the monitor. It makes the monitor due (NextCheckAtUtc = now) only
/// when there is something to decide (first ping, failure, recovery, status change) or the last evaluation is older than
/// interval / <see cref="EvaluationsPerInterval"/>; otherwise it only moves the deadline. So a job that pings far more
/// often than its interval costs one evaluation per interval / N, not one per ping. Version is bumped so a check
/// being saved concurrently retries against the fresh row.</item>
/// </list>
/// </summary>
public static partial class HeartbeatEndpoint
{
    public const string Pattern = "/hb/{token}";
    public const string PathPrefix = "/hb";
    public const string ResponseBody = "OK";
    public const int MaxMessageLength = 200;
    public const int EvaluationsPerInterval = 10;

    public static IServiceCollection AddHeartbeatEndpoint(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HeartbeatOptions>(config.GetSection(HeartbeatOptions.Section));
        services.AddSingleton<HeartbeatRateLimiter>();
        services.AddSingleton<HeartbeatUrlReveal>();
        return services;
    }

    public static IEndpointRouteBuilder MapHeartbeatEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapMethods(Pattern, [HttpMethods.Get, HttpMethods.Post, HttpMethods.Head], HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
        return app;
    }

    /// <summary>
    /// Full ping URL for <paramref name="token"/>, built by <see cref="PublicUrls"/>: <c>Site:PublicBaseUrl</c> when
    /// configured (required outside Development), otherwise the current request's scheme, host and path base.
    /// </summary>
    public static string PingUrl(HttpRequest request, string token) =>
        request.HttpContext.RequestServices.GetRequiredService<PublicUrls>().Build(request, "/hb/" + token);

    private static async Task<IResult> HandleAsync(string token, HttpContext context, AppDbContext db, HeartbeatRateLimiter limiter, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        var hash = HeartbeatTokens.Hash(token);
        // After a restart the known-token set is rebuilt from the database once, so a flood of unknown tokens running
        // at that moment cannot lock out existing monitors.
        await limiter.EnsureWarmAsync(ct => db.Monitors.AsNoTracking()
            .Where(x => x.Type == MonitorType.Heartbeat && x.HeartbeatTokenHash != null)
            .Select(x => x.HeartbeatTokenHash!).ToListAsync(ct), context.RequestAborted);
        var decision = limiter.TryAcquire(hash, ApiSetup.ClientPartition(context.Connection.RemoteIpAddress));
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Text("Too many requests", "text/plain", Encoding.UTF8, StatusCodes.Status429TooManyRequests);
        }

        if (HeartbeatTokens.LooksValid(token))
        {
            var now = time.GetUtcNow().UtcDateTime;
            var failed = string.Equals(context.Request.Query["status"].ToString(), "fail", StringComparison.OrdinalIgnoreCase);
            var message = CleanMessage(context.Request.Query["msg"].ToString());
            var m = await db.Monitors.AsNoTracking()
                .Where(x => x.HeartbeatTokenHash == hash && x.Type == MonitorType.Heartbeat)
                .Select(x => new PingTarget(x.Id, x.Status, x.IsEnabled && x.Status != MonitorStatus.Paused && x.Customer.IsActive,
                    x.IntervalSeconds, x.GracePeriodSeconds, x.LastCheckedAtUtc, x.LastHeartbeatFailed, x.NextCheckAtUtc))
                .FirstOrDefaultAsync(context.RequestAborted);
            if (m is not null) limiter.MarkKnown(hash);
            if (m is { Active: true })
            {
                var next = NextCheckAfterPing(m, failed, now);
                await db.Monitors.Where(x => x.Id == m.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastHeartbeatAtUtc, now)
                        .SetProperty(x => x.LastHeartbeatFailed, failed)
                        .SetProperty(x => x.LastHeartbeatMessage, message)
                        .SetProperty(x => x.NextCheckAtUtc, next)
                        .SetProperty(x => x.Version, x => x.Version + 1), context.RequestAborted);
            }
        }
        return Results.Text(ResponseBody, "text/plain", Encoding.UTF8);
    }

    public sealed record PingTarget(int Id, MonitorStatus Status, bool Active, int IntervalSeconds, int GracePeriodSeconds,
        DateTime? LastCheckedAtUtc, bool LastHeartbeatFailed, DateTime? NextCheckAtUtc);

    /// <summary>NextCheckAtUtc after a ping at <paramref name="now"/> (see the class remarks).</summary>
    public static DateTime NextCheckAfterPing(PingTarget m, bool failed, DateTime now)
    {
        var minGap = TimeSpan.FromSeconds(Math.Max(1, m.IntervalSeconds / EvaluationsPerInterval));
        var evaluate = failed || m.LastHeartbeatFailed || m.Status != MonitorStatus.Up
                       || m.LastCheckedAtUtc is not { } last || now - last >= minGap;
        if (evaluate) return now;
        if (m.NextCheckAtUtc is { } pending && pending <= now) return pending; // an evaluation is already due
        return now + TimeSpan.FromSeconds(Math.Max(1, m.IntervalSeconds) + Math.Max(0, m.GracePeriodSeconds));
    }

    /// <summary>
    /// Trace enrichment for incoming requests: a ping span keeps the route but loses the token (path) and the query
    /// (message). Runs after the instrumentation has set its URL tags.
    /// </summary>
    public static void RedactTrace(Activity activity, HttpRequest request)
    {
        if (!request.Path.StartsWithSegments(PathPrefix)) return;
        activity.SetTag("url.path", Pattern);
        activity.SetTag("url.query", null);
        activity.SetTag("url.full", null);
        activity.SetTag("http.target", null);
        activity.DisplayName = request.Method + " " + Pattern;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    /// <summary>Long opaque strings (API keys, tokens, hashes) are hidden.</summary>
    [GeneratedRegex(@"[A-Za-z0-9+/=_\-]{24,}")]
    private static partial Regex OpaqueRx();

    /// <summary>Single line, no control characters, URLs and long opaque strings masked, at most <see cref="MaxMessageLength"/> characters.</summary>
    public static string? CleanMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Length > 4 * MaxMessageLength ? raw[..(4 * MaxMessageLength)] : raw;
        s = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        s = UrlRx().Replace(s, m => SecretMasker.MaskUrl(m.Value));
        s = OpaqueRx().Replace(s, SecretMasker.Ellipsis);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Monitoring.Alerting.AlertText.Cut(s, MaxMessageLength);
        return s.Length == 0 ? null : s;
    }
}

/// <summary>
/// Limits for heartbeat pings, all fixed windows: per token hash (per minute), per client partition
/// (<see cref="ApiSetup.ClientPartition"/>, per minute) and, for unknown tokens only, a global flood guard (per second).
/// In memory, single instance.
/// <para>
/// Known tokens (hashes that matched a monitor in the last <see cref="KnownFor"/>, issued in this process, or loaded once
/// from the database after a restart) bypass the global guard and the memory-pressure check: an attacker spraying
/// random tokens from many addresses (an IPv6 /48 has 65536 /64s) can exhaust those, but must never block the pings of
/// real monitors and cause false DOWNs. Per-token limits always apply.
/// </para>
/// Memory is bounded: counters are evicted by age only; when the table is still full, unknown tokens needing a new
/// entry are denied (never fail open by forgetting existing counters). The known set holds hashes of real monitors only.
/// </summary>
public sealed class HeartbeatRateLimiter(TimeProvider time, Microsoft.Extensions.Options.IOptions<HeartbeatOptions> options)
{
    public const int DefaultMaxEntries = 100_000;
    public const int MaxKnownTokens = 200_000;
    /// <summary>Two of the longest heartbeat intervals (24 h).</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(2);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    public readonly record struct Decision(bool Allowed, int RetryAfterSeconds);

    private readonly HeartbeatOptions _o = options.Value;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime Start, int Count)> _windows = new();
    private readonly Dictionary<string, DateTime> _known = new();
    private (DateTime Start, int Count) _global;
    private int _warm; // 0 = not loaded, 1 = loading / loaded

    /// <summary>Upper bound of tracked partitions + unknown tokens (tests lower it).</summary>
    public int MaxEntries { get; init; } = DefaultMaxEntries;

    public int TrackedEntries { get { lock (_lock) return _windows.Count; } }

    /// <summary>Loads the hashes of all heartbeat monitors once per process (first ping after start).</summary>
    public async Task EnsureWarmAsync(Func<CancellationToken, Task<List<string>>> loadHashes, CancellationToken ct)
    {
        if (Volatile.Read(ref _warm) == 1 || Interlocked.Exchange(ref _warm, 1) == 1) return;
        try
        {
            foreach (var hash in await loadHashes(ct)) MarkKnown(hash);
        }
        catch
        {
            Volatile.Write(ref _warm, 0); // try again on a later ping
            throw;
        }
    }

    /// <summary>The token belongs to a monitor (lookup matched, or just issued).</summary>
    public void MarkKnown(string tokenHash)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (_known.Count >= MaxKnownTokens && !_known.ContainsKey(tokenHash))
            {
                foreach (var key in _known.Where(kv => now - kv.Value >= KnownFor).Select(kv => kv.Key).ToList()) _known.Remove(key);
                if (_known.Count >= MaxKnownTokens) return;
            }
            _known[tokenHash] = now;
        }
    }

    public bool IsKnown(string tokenHash)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock) return _known.TryGetValue(tokenHash, out var seen) && now - seen < KnownFor;
    }

    public Decision TryAcquire(string tokenHash, string clientPartition)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            var known = _known.TryGetValue(tokenHash, out var seen) && now - seen < KnownFor;
            if (!known)
            {
                if (now - _global.Start >= Second) _global = (now, 0);
                if (_global.Count >= Math.Max(1, _o.PingsPerSecondGlobal)) return new Decision(false, 1);
            }

            var tokenKey = "t:" + tokenHash;
            var clientKey = "c:" + clientPartition;
            var c = Current(clientKey, now);
            var t = Current(tokenKey, now);
            if (t.Count >= Math.Max(1, _o.PingsPerMinutePerToken)) return Deny(t.Start, now);
            if (c.Count >= Math.Max(1, _o.PingsPerMinutePerClient)) return Deny(c.Start, now);

            var newEntries = (_windows.ContainsKey(clientKey) ? 0 : 1) + (_windows.ContainsKey(tokenKey) ? 0 : 1);
            if (newEntries > 0 && _windows.Count + newEntries > MaxEntries)
            {
                EvictExpired(now);
                // Under pressure: deny unknown tokens, keep all counters; known tokens may exceed the cap (bounded by the known set).
                if (!known && _windows.Count + newEntries > MaxEntries) return new Decision(false, 60);
            }

            if (!known) _global = (_global.Start, _global.Count + 1);
            _windows[clientKey] = (c.Start, c.Count + 1);
            _windows[tokenKey] = (t.Start, t.Count + 1);
            return new Decision(true, 0);
        }
    }

    private (DateTime Start, int Count) Current(string key, DateTime now) =>
        _windows.TryGetValue(key, out var w) && now - w.Start < Minute ? w : (now, 0);

    private static Decision Deny(DateTime start, DateTime now) =>
        new(false, Math.Max(1, (int)Math.Ceiling((start + Minute - now).TotalSeconds)));

    private void EvictExpired(DateTime now)
    {
        foreach (var key in _windows.Where(kv => now - kv.Value.Start >= Minute).Select(kv => kv.Key).ToList())
            _windows.Remove(key);
    }
}
