using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Optional X-Forwarded-For / X-Forwarded-Proto processing for running behind a TLS-terminating reverse proxy
/// (off by default). Settings:
/// <list type="bullet">
/// <item><c>ForwardedHeaders:Enabled</c> (false)</item>
/// <item><c>ForwardedHeaders:KnownProxies</c>: proxy IPs whose headers are trusted (comma separated or an array)</item>
/// <item><c>ForwardedHeaders:KnownNetworks</c>: trusted proxy networks in CIDR form, e.g. <c>10.0.0.0/8</c></item>
/// <item><c>ForwardedHeaders:ForwardLimit</c> (1): number of proxy hops to unwind</item>
/// </list>
/// Headers from any other address are ignored (with neither list set, only loopback is trusted, the framework default).
/// With it, the client IP (per-IP API rate limit, logs), the https scheme (Secure cookie, HTTPS redirection) are correct.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static void UseAppForwardedHeaders(this WebApplication app)
    {
        var section = app.Configuration.GetSection("ForwardedHeaders");
        if (!section.GetValue("Enabled", false)) return;

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = section.GetValue("ForwardLimit", 1)
        };
        var proxies = Values(section.GetSection("KnownProxies"));
        var networks = Values(section.GetSection("KnownNetworks"));
        if (proxies.Count > 0 || networks.Count > 0)
        {
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var p in proxies) options.KnownProxies.Add(IPAddress.Parse(p));
            foreach (var n in networks)
            {
                var net = System.Net.IPNetwork.Parse(n);
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(net.BaseAddress, net.PrefixLength));
            }
        }
        app.UseForwardedHeaders(options);
    }

    /// <summary>"a, b" or an array section (KnownProxies:0, KnownProxies:1, ...).</summary>
    private static List<string> Values(IConfigurationSection s) =>
        (s.Value is string single ? single.Split(',') : s.GetChildren().Select(c => c.Value ?? ""))
        .Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
}
