using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Retention;
using TriggerCheck.Monitoring.Scheduling;
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

        // ---- HTTP clients ----
        services.AddHttpClient(MonitoringHttpClients.Probe, ConfigureProbeClient)
            .ConfigurePrimaryHttpMessageHandler(() => CreateProbeHandler(followRedirects: true));
        services.AddHttpClient(MonitoringHttpClients.ProbeNoRedirect, ConfigureProbeClient)
            .ConfigurePrimaryHttpMessageHandler(() => CreateProbeHandler(followRedirects: false));
        services.AddHttpClient(MonitoringHttpClients.Webhook, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(10);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(MonitoringHttpClients.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

        // ---- checkers ----
        services.AddSingleton<IMonitorChecker, HttpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, KeywordMonitorChecker>();
        services.AddSingleton<IMonitorChecker, TcpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, PingMonitorChecker>();

        // ---- engine ----
        services.AddSingleton<MonitorRunGate>();
        services.AddScoped<IMonitorCheckRunner, MonitorCheckRunner>();

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

    private static SocketsHttpHandler CreateProbeHandler(bool followRedirects) => new()
    {
        AllowAutoRedirect = followRedirects,
        MaxAutomaticRedirections = MonitoringHttpClients.MaxRedirects,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(30)
    };
}
