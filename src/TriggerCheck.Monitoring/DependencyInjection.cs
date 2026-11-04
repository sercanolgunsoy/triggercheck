using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Escalation;
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
        services.Configure<AlertingOptions>(config.GetSection(AlertingOptions.Section));
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.Section));
        services.Configure<TwilioOptions>(config.GetSection(TwilioOptions.Section));
        services.Configure<EscalationOptions>(config.GetSection(EscalationOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.TryAddSingleton<MonitoringMetrics>();

        services.AddScoped<IUptimeStatsService, UptimeStatsService>();
        services.AddMemoryCache();
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
            })
            // Alert URLs carry secrets (Slack/Teams webhook paths, Telegram bot tokens): no request URI logging.
            .RemoveAllLoggers();

        // ---- checkers ----
        services.AddSingleton<IMonitorChecker, HttpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, KeywordMonitorChecker>();
        services.AddSingleton<IMonitorChecker, TcpMonitorChecker>();
        services.AddSingleton<IMonitorChecker, PingMonitorChecker>();

        // ---- engine ----
        services.AddSingleton<MonitorRunGate>();
        // Checkers are wrapped at resolve time so every probe is traced and measured (OPS-4, see MonitoringMetrics).
        services.AddScoped<IMonitorCheckRunner>(sp => ActivatorUtilities.CreateInstance<MonitorCheckRunner>(sp, InstrumentedMonitorChecker.Wrap(
            sp.GetServices<IMonitorChecker>(), sp.GetRequiredService<MonitoringMetrics>(), sp.GetRequiredService<TimeProvider>())));
        services.AddScoped<MonitorLifecycle>();

        // ---- alerting ----
        services.AddSingleton<INotifier, EmailNotifier>();
        services.AddSingleton<INotifier, WebhookNotifier>();
        services.AddSingleton<INotifier, SlackNotifier>();
        services.AddSingleton<INotifier, TeamsNotifier>();
        services.AddSingleton<INotifier, TelegramNotifier>();
        services.AddSingleton<INotifier, TwilioSmsNotifier>();
        services.AddSingleton<INotifier, TwilioVoiceNotifier>();
        services.AddScoped<IAlertDispatcher, AlertDispatcher>();
        // Runner -> bounded queue -> sender (own concurrency, timeouts, retries). Registered before the scheduler so
        // it stops after it (hosted services stop in reverse order) and can drain the last alerts.
        services.AddSingleton<AlertDeliveryThrottle>();
        services.AddSingleton<AlertQueue>();
        services.AddSingleton<IAlertQueue>(sp => sp.GetRequiredService<AlertQueue>());
        services.AddSingleton<AlertSenderService>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertSenderService>());

        // ---- incidents: acknowledgement + escalation (FEAT-5) ----
        services.AddSingleton<IncidentAckLinks>();
        services.AddScoped<IncidentAcknowledger>();
        services.AddScoped<EscalationRunner>();
        services.AddHostedService<EscalationService>();

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
