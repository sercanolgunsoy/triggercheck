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
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Ai.Narrative;
using TriggerCheck.Monitoring.Reporting;
using TriggerCheck.Ai.Qa;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Cluster;

namespace TriggerCheck.Ai;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the AI layer (AI-0, AI-1, AI-2, AI-3, AI-4, AI-5, AI-6, AI-7, AI-9, AI-10, AI-12). The real Claude client is used only when Ai:Enabled is true AND a key
    /// is configured (Ai:ApiKey or ANTHROPIC_API_KEY); otherwise <see cref="UnavailableAiClient"/> reports "AI
    /// unavailable" and every job is recorded as skipped. Tests replace <see cref="IAiClient"/> with
    /// <see cref="FakeAiClient"/>. The monitoring layer does not depend on this project.
    /// </summary>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AiOptions>().Bind(config.GetSection(AiOptions.Section))
            .Validate(o => !o.InvalidEfforts().Any(), "Ai:Effort / Ai:Features:*:Effort must be one of: low, medium, high, xhigh, max.")
            .Validate(o => !o.Enabled || !o.Narrative.Enabled || o.Narrative.RunningTimeoutMinutes * 60L > 2L * o.TimeoutSeconds,
                "Ai:Narrative:RunningTimeoutMinutes must be longer than two AI calls (Ai:TimeoutSeconds), or a slow call is taken over and paid twice.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddClusterLeases(); // OPS-3: the watcher and the baseline job run on one instance at a time

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
        // AI-4: incident correlation. L1 rules + proposals (A1); the alert-path hook groups notifications only at A3.
        services.AddOptions<AiCorrelationOptions>().Bind(config.GetSection(AiCorrelationOptions.Section));
        services.TryAddSingleton<CorrelationAutonomy>();
        services.TryAddSingleton<CorrelationRuntimeState>();
        services.AddScoped<IncidentCorrelationService>();
        services.AddScoped<IAiContextProvider, CorrelationGroupingProvider>();
        services.AddScoped<IAlertGrouping, CorrelationAlertGrouping>();
        services.AddScoped<CorrelationScanner>();
        services.AddHostedService<CorrelationWatcher>();
        // AI-5: rule-based settings suggestions (L1) and their AI explanation.
        services.AddScoped<IAiContextProvider, SettingsSuggestionProvider>();
        services.AddScoped<SettingsSuggestionJob>();
        services.AddHostedService<SettingsSuggestionHostedService>();

        // AI-9: monitor setup assistant (fills the create form on request; never saves).
        services.AddScoped<Features.MonitorSetup.MonitorSetupAssistant>();

        // AI-12: SuperAdmin platform insights (rule-based signals; the AI summary only on request, platform budget).
        services.AddScoped<Insights.InsightDataLoader>();
        services.AddScoped<Insights.PlatformInsights>();
        services.AddScoped<Insights.InsightSummaryService>();

        services.AddScoped<BaselineJob>();
        services.AddHostedService<BaselineHostedService>();

        services.AddQuestionAnswer(); // AI-7
        return services;
    }

    /// <summary>
    /// AI-7 natural-language Q&amp;A: its own tool-use client (real only when AI is enabled and a key is configured, like
    /// <see cref="IAiClient"/>), the read-only toolbox, the per-user limiter and the number-grounding hook (replaceable).
    /// </summary>
    private static void AddQuestionAnswer(this IServiceCollection services)
    {
        services.AddOptions<AiOptions>()
            .Validate(o => AiOptions.Efforts.Contains((o.Qa.Effort ?? "").Trim().ToLowerInvariant()), "Ai:Qa:Effort must be one of: low, medium, high, xhigh, max.");
        services.AddSingleton<IAiToolClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AiOptions>>().Value;
            return o.Enabled && o.ResolveApiKey() is not null
                ? new AnthropicAiToolClient(sp.GetRequiredService<IOptions<AiOptions>>(), sp.GetRequiredService<ILogger<AnthropicAiToolClient>>())
                : new UnavailableAiToolClient();
        });
        services.AddSingleton<QaRateLimiter>();
        services.TryAddSingleton<IQaAnswerGrounding, QaNumberGrounding>();
        services.AddScoped<QaToolbox>();
        services.AddScoped<IQuestionAnswerService, QuestionAnswerService>();
    }
}
