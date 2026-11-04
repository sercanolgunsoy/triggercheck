using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Retention;
using TriggerCheck.Monitoring.Scheduling;
using TriggerCheck.Monitoring.Security;
using TriggerCheck.Monitoring.Stats;

namespace TriggerCheck.Monitoring;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the monitoring engine (checkers, scheduler hosted service, alerting, retention) and stats services.
    /// OWNED BY: monitoring-engine workstream. Web's Program.cs only calls this method.
    /// Hosted services are always registered but return immediately when Monitoring:Enabled=false;
    /// <see cref="IMonitorCheckRunner"/> ("check now") works regardless.
    /// </summary>
    public static IServiceCollection AddMonitoringEngine(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MonitoringOptions>(config.GetSection(MonitoringOptions.Section));
        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IUptimeStatsService, UptimeStatsService>();
        services.AddScoped<IResponseTimeSeriesService, ResponseTimeSeriesService>();

        // ---- HTTP clients ----
        // Every connection (incl. redirect hops) goes through TargetGuard, which resolves DNS itself and refuses
        // private/internal addresses unless Monitoring:AllowPrivateTargets is set (SSRF protection).
        services.AddHttpClient(MonitoringHttpClients.Probe, ConfigureProbeClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateProbeHandler(sp, followRedirects: true));
        services.AddHttpClient(MonitoringHttpClients.ProbeNoRedirect, ConfigureProbeClient)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateProbeHandler(sp, followRedirects: false));
        services.AddHttpClient(MonitoringHttpClients.Webhook, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(10);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(MonitoringHttpClients.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectCallback = TargetGuard.ConnectCallback(AllowPrivateTargets(sp), GuardLogger(sp))
            });

        // ---- checkers ----
        services.AddSingleton<IMonitorChecker, HttpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, KeywordMonitorChecker>();
        services.AddSingleton<IMonitorChecker, TcpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, PingMonitorChecker>();

        // ---- engine ----
        services.AddSingleton<MonitorRunGate>();
        services.AddScoped<IMonitorCheckRunner, MonitorCheckRunner>();
        services.AddScoped<MonitorLifecycle>();

        // ---- alerting ----
        services.AddSingleton<INotifier, EmailNotifier>();
        services.AddSingleton<INotifier, WebhookNotifier>();
        services.AddScoped<IAlertDispatcher, AlertDispatcher>();

        // ---- background jobs ----
        services.AddScoped<RetentionCleaner>();
        services.AddSingleton<MonitorScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<MonitorScheduler>());
        services.AddHostedService<RetentionCleanupService>();

        return services;
    }

    private static void ConfigureProbeClient(HttpClient c)
    {
        c.Timeout = Timeout.InfiniteTimeSpan; // per-monitor timeout is enforced by the checker
        c.DefaultRequestHeaders.UserAgent.ParseAdd(MonitoringHttpClients.UserAgent);
    }

    private static ILogger GuardLogger(IServiceProvider sp) =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TargetGuard).FullName!);

    private static Func<bool> AllowPrivateTargets(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptionsMonitor<MonitoringOptions>>();
        return () => options.CurrentValue.AllowPrivateTargets;
    }

    private static SocketsHttpHandler CreateProbeHandler(IServiceProvider sp, bool followRedirects) => new()
    {
        // Same SSRF guard as TargetGuard.ConnectCallback, plus phase timings and TLS certificate capture (ENG-8, FEAT-1).
        ConnectCallback = ProbeConnection.ConnectCallback(AllowPrivateTargets(sp), GuardLogger(sp)),
        // No proxy: the connect-time guard must see the real destination, not the proxy's address.
        UseProxy = false,
        AllowAutoRedirect = followRedirects,
        MaxAutomaticRedirections = MonitoringHttpClients.MaxRedirects,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(30)
    };
}
