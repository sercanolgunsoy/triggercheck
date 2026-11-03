using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Monitoring.Stats;

namespace TriggerCheck.Monitoring;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the monitoring engine (checkers, scheduler hosted service, alerting) and stats services.
    /// OWNED BY: monitoring-engine workstream. Web's Program.cs only calls this method.
    /// </summary>
    public static IServiceCollection AddMonitoringEngine(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MonitoringOptions>(config.GetSection(MonitoringOptions.Section));
        services.AddScoped<IUptimeStatsService, UptimeStatsService>();
        // TODO(monitoring-engine): register checkers, IMonitorCheckRunner, scheduler BackgroundService, notifiers.
        return services;
    }
}
