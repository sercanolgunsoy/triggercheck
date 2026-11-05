using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring;

namespace TriggerCheck.ProbeAgent;

public static class AgentHosting
{
    /// <summary>
    /// Registers the agent: the shared probe checkers (section <c>Monitoring</c>: AllowPrivateTargets defaults to false,
    /// so the SSRF guard is on), the central API client and the worker (also registered as hosted service).
    /// </summary>
    public static IServiceCollection AddProbeAgent(this IServiceCollection services, IConfiguration config)
    {
        services.AddProbeCheckers(config);
        services.Configure<AgentOptions>(config.GetSection(AgentOptions.Section));
        services.AddHttpClient(AgentApiClient.ClientName, (sp, c) =>
            {
                var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                // Trailing slash: request paths are relative, so a path base in CentralUrl is kept.
                if (Uri.TryCreate(o.CentralUrl.TrimEnd('/') + "/", UriKind.Absolute, out var central)) c.BaseAddress = central;
                c.Timeout = TimeSpan.FromSeconds(30);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("TriggerCheck-ProbeAgent/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false };
                if (!string.IsNullOrEmpty(o.ClientCertificatePath))
                {
                    // Optional mutual TLS (verified by a reverse proxy in front of the main app).
                    handler.SslOptions.ClientCertificates = [X509CertificateLoader.LoadPkcs12FromFile(o.ClientCertificatePath, o.ClientCertificatePassword)];
                }
                return handler;
            })
            // The client carries the agent credential: no request logging.
            .RemoveAllLoggers();
        services.AddSingleton<AgentApiClient>();
        services.AddSingleton<ProbeAgentWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<ProbeAgentWorker>());
        return services;
    }
}

/// <summary>Entry point of the probe agent container (Dockerfile.probe).</summary>
public static class AgentProgram
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddProbeAgent(builder.Configuration);
        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TriggerCheck.ProbeAgent");
        var errors = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value.Validate().ToList();
        if (errors.Count > 0)
        {
            foreach (var e in errors) logger.LogCritical("Invalid configuration: {Error}", e);
            return 1;
        }
        if (host.Services.GetRequiredService<IOptions<MonitoringOptions>>().Value.AllowPrivateTargets)
            logger.LogWarning("Monitoring:AllowPrivateTargets is enabled: SSRF protection is OFF and probes can reach internal addresses " +
                              "of this region's network. Use this only for development or testing.");

        await host.RunAsync();
        return 0;
    }
}
