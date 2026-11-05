using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Baseline;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Ai.Features;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Monitoring.Reporting;

namespace TriggerCheck.Ai;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the AI layer (AI-0, AI-1, AI-2, AI-3, AI-6, AI-10). The real Claude client is used only when Ai:Enabled is true AND a key
    /// is configured (Ai:ApiKey or ANTHROPIC_API_KEY); otherwise <see cref="UnavailableAiClient"/> reports "AI
    /// unavailable" and every job is recorded as skipped. Tests replace <see cref="IAiClient"/> with
    /// <see cref="FakeAiClient"/>. The monitoring layer does not depend on this project.
    /// </summary>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section))
            .Validate(o => !o.InvalidEfforts().Any(), "Ai:Effort / Ai:Features:*:Effort must be one of: low, medium, high, xhigh, max.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IAiClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            if (o.Enabled && o.ResolveApiKey() is not null)
                return ActivatorUtilities.CreateInstance<AnthropicAiClient>(sp);
            if (o.Enabled)
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("TriggerCheck.Ai")
                    .LogWarning("Ai:Enabled is true but no API key is configured (Ai:ApiKey / ANTHROPIC_API_KEY); AI features are unavailable.");
            return new UnavailableAiClient();
        });

        services.TryAddSingleton<IAiOutputValidator, AiOutputValidator>();
        services.AddScoped<AiTenantData>();
        services.AddScoped<IAiUsage, AiUsageService>();
        services.AddScoped<IAiInterpreter, AiInterpreter>();

        services.AddSingleton<AiJobQueue>();
        services.AddSingleton<IAiJobQueue>(sp => sp.GetRequiredService<AiJobQueue>());
        services.AddHostedService<AiWorkerService>();

        // AI-2 / AI-3: context providers and the event watcher that queues their jobs (never on the alert path).
        services.AddScoped<IAiContextProvider, IncidentInterpretationProvider>();
        services.AddScoped<IAiContextProvider, SlowdownExplanationProvider>();
        services.AddScoped<AiEventScanner>();
        services.AddHostedService<AiEventWatcher>();

        // AI-6: narrative SLA reports (batch job; the report page and the monthly e-mail only read).
        services.TryAddScoped<SlaReportService>();
        services.AddScoped<IAiContextProvider, NarrativeReportProvider>();
        services.AddScoped<NarrativeReportJob>();
        services.AddScoped<NarrativeReports>();
        services.AddHostedService<NarrativeReportService>();

        services.AddScoped<BaselineJob>();
        services.AddHostedService<BaselineHostedService>();
        return services;
    }
}
