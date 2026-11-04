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

namespace TriggerCheck.Ai;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the AI layer (AI-0, AI-1, AI-10). The real Claude client is used only when Ai:Enabled is true AND a key
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

        services.AddScoped<AiTenantData>();
        services.AddScoped<IAiUsage, AiUsageService>();
        services.AddScoped<IAiInterpreter, AiInterpreter>();

        services.AddSingleton<AiJobQueue>();
        services.AddSingleton<IAiJobQueue>(sp => sp.GetRequiredService<AiJobQueue>());
        services.AddHostedService<AiWorkerService>();

        services.AddScoped<BaselineJob>();
        services.AddHostedService<BaselineHostedService>();
        return services;
    }
}
