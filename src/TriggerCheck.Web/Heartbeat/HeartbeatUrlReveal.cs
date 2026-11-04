using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace TriggerCheck.Web.Heartbeat;

/// <summary>
/// One-time reveal of a freshly issued heartbeat ping URL in the panel (same flow as API keys): the POST that issued the
/// token parks the URL for 5 minutes in a server-side store (IMemoryCache, never TempData / a cookie), bound to the
/// user, the customer, the monitor and a per-issue reveal id, and redirects; the next GET of the details page takes it
/// out (so a refresh does not show it again) and is sent with <c>Cache-Control: no-store</c>.
/// </summary>
public sealed class HeartbeatUrlReveal(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static string Key(ClaimsPrincipal user, int customerId, int monitorId, Guid revealId) =>
        $"hb-reveal:{user.FindFirstValue(ClaimTypes.NameIdentifier)}:{customerId}:{monitorId}:{revealId:N}";

    public Guid Park(ClaimsPrincipal user, int customerId, int monitorId, string pingUrl)
    {
        var revealId = Guid.NewGuid();
        cache.Set(Key(user, customerId, monitorId, revealId), pingUrl, Lifetime);
        return revealId;
    }

    public string? Take(ClaimsPrincipal user, int customerId, int monitorId, Guid? revealId)
    {
        if (revealId is not Guid id) return null;
        var key = Key(user, customerId, monitorId, id);
        if (!cache.TryGetValue(key, out string? url)) return null;
        cache.Remove(key);
        return url;
    }
}
