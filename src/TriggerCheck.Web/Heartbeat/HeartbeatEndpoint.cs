using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Checks;

namespace TriggerCheck.Web.Heartbeat;

/// <summary>
/// Ping endpoint of heartbeat (push) monitors: <c>GET|POST|HEAD /hb/{token}</c>, optionally <c>?status=fail</c> (the
/// job reports a failure: counts as a failed check right away) and <c>&amp;msg=...</c> (short message, truncated and
/// masked; nothing else of the request, and never the body, is stored).
/// <list type="bullet">
/// <item>Anonymous (AllowAnonymous: no SessionValidator lookup, no cookies needed, so there is nothing to forge
/// cross-site; the secret is the token itself).</item>
/// <item>No existence oracle: known, unknown and malformed tokens get the same 200 response and body. Only the
/// rate limit (keyed by the token hash and the client IP, independent of whether the token exists) answers 429.</item>
/// <item>A ping only records the time / failure flag on the monitor and makes it due (NextCheckAtUtc = now); the check
/// runner then evaluates it through the normal state machine (incident, alerts). Version is bumped so a check that is
/// being saved concurrently retries against the fresh row and does not overwrite the new schedule.</item>
/// </list>
/// </summary>
public static partial class HeartbeatEndpoint
{
    public const string Pattern = "/hb/{token}";
    public const string ResponseBody = "OK";
    public const int MaxMessageLength = 200;

    public static IServiceCollection AddHeartbeatEndpoint(this IServiceCollection services) =>
        services.AddSingleton<HeartbeatRateLimiter>();

    public static IEndpointRouteBuilder MapHeartbeatEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapMethods(Pattern, [HttpMethods.Get, HttpMethods.Post, HttpMethods.Head], HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
        return app;
    }

    /// <summary>Full ping URL for <paramref name="token"/> as seen by the current request (scheme, host, path base).</summary>
    public static string PingUrl(HttpRequest request, string token) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/hb/{token}";

    private static async Task<IResult> HandleAsync(string token, HttpContext context, AppDbContext db, HeartbeatRateLimiter limiter, TimeProvider time)
    {
        context.Response.Headers.CacheControl = "no-store";
        var hash = HeartbeatTokens.Hash(token);
        var decision = limiter.TryAcquire(hash, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Text("Too many requests", "text/plain", Encoding.UTF8, StatusCodes.Status429TooManyRequests);
        }

        if (HeartbeatTokens.LooksValid(token))
        {
            var now = time.GetUtcNow().UtcDateTime;
            var failed = string.Equals(context.Request.Query["status"].ToString(), "fail", StringComparison.OrdinalIgnoreCase);
            var message = CleanMessage(context.Request.Query["msg"].ToString());
            await db.Monitors
                .Where(m => m.HeartbeatTokenHash == hash && m.Type == MonitorType.Heartbeat)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.LastHeartbeatAtUtc, now)
                    .SetProperty(m => m.LastHeartbeatFailed, failed)
                    .SetProperty(m => m.LastHeartbeatMessage, message)
                    .SetProperty(m => m.NextCheckAtUtc, now)
                    .SetProperty(m => m.Version, m => m.Version + 1), context.RequestAborted);
        }
        return Results.Text(ResponseBody, "text/plain", Encoding.UTF8);
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
        if (s.Length > MaxMessageLength) s = s[..(MaxMessageLength - 1)] + SecretMasker.Ellipsis;
        return s.Length == 0 ? null : s;
    }
}

/// <summary>
/// Fixed-window limits for heartbeat pings: <see cref="PerTokenPerMinute"/> per token (hash) and
/// <see cref="PerIpPerMinute"/> per client IP. In memory, single instance (like ManualCheckLimiter); bounded size.
/// </summary>
public sealed class HeartbeatRateLimiter(TimeProvider time)
{
    public const int PerTokenPerMinute = 10;
    public const int PerIpPerMinute = 120;
    private const int MaxEntries = 50_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public readonly record struct Decision(bool Allowed, int RetryAfterSeconds);

    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTime Start, int Count)> _windows = new();

    public Decision TryAcquire(string tokenHash, string ip)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (_windows.Count >= MaxEntries) Prune(now);
            var tokenKey = "t:" + tokenHash;
            var ipKey = "i:" + ip;
            var t = Current(tokenKey, now);
            var i = Current(ipKey, now);
            if (t.Count >= PerTokenPerMinute) return Deny(t.Start, now);
            if (i.Count >= PerIpPerMinute) return Deny(i.Start, now);
            _windows[tokenKey] = (t.Start, t.Count + 1);
            _windows[ipKey] = (i.Start, i.Count + 1);
            return new Decision(true, 0);
        }
    }

    private (DateTime Start, int Count) Current(string key, DateTime now) =>
        _windows.TryGetValue(key, out var w) && now - w.Start < Window ? w : (now, 0);

    private static Decision Deny(DateTime start, DateTime now) =>
        new(false, Math.Max(1, (int)Math.Ceiling((start + Window - now).TotalSeconds)));

    private void Prune(DateTime now)
    {
        foreach (var key in _windows.Where(kv => now - kv.Value.Start >= Window).Select(kv => kv.Key).ToList())
            _windows.Remove(key);
        if (_windows.Count >= MaxEntries) _windows.Clear(); // flood of distinct keys: fail open rather than grow
    }
}
