using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// Real Claude API client (official Anthropic C# SDK). One non-streaming Messages call per request with:
/// structured outputs (output_config.format = the interpretation JSON schema), effort from config, adaptive thinking
/// (the model default), and a prompt-cache breakpoint on the static system prompt. Retries (408/409/429/5xx,
/// connection errors) are the SDK's; the overall timeout is <see cref="AiOptions.TimeoutSeconds"/>.
/// Refusals and max_tokens are reported, never parsed. No server-side model fallback is configured on purpose: every
/// model change must pass the eval gate (AI-11) first.
/// Logs never contain prompt or response text (tenant data).
/// </summary>
public sealed class AnthropicAiClient : IAiClient
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicAiClient> _logger;

    public AnthropicAiClient(IOptions<AiOptions> options, ILogger<AnthropicAiClient> logger)
    {
        var o = options.Value;
        _logger = logger;
        _client = new AnthropicClient
        {
            ApiKey = o.ResolveApiKey() ?? throw new InvalidOperationException("No Claude API key configured."),
            MaxRetries = Math.Max(0, o.MaxRetries),
            // Per attempt: the SDK retries inside the overall Ai:TimeoutSeconds cap enforced by the interpreter.
            Timeout = AttemptTimeout(o),
        };
    }

    public bool IsAvailable => true;

    /// <summary>Ai:TimeoutSeconds split over the attempts (1 + Ai:MaxRetries), at least 5 seconds each.</summary>
    public static TimeSpan AttemptTimeout(AiOptions o) =>
        TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds / (Math.Max(0, o.MaxRetries) + 1)));

    public async Task<AiCallResult> CompleteAsync(AiCallRequest request, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            // Static prefix (instructions) first with a cache breakpoint; the variable data follows in the user turn.
            System = new List<TextBlockParam>
            {
                new() { Text = request.SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new UserMessageParam { Content = request.UserContent }],
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(request.Effort),
                Format = new JsonOutputFormat { Schema = new Dictionary<string, System.Text.Json.JsonElement>(request.OutputSchema) },
            },
        };

        Message response;
        try
        {
            response = await _client.Messages.Create(parameters, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Claude API call for {Feature} timed out", request.Feature);
            return AiCallResult.Fail("timeout");
        }
        catch (AnthropicRateLimitException ex)
        {
            _logger.LogWarning(ex, "Claude API rate limit for {Feature}", request.Feature);
            return AiCallResult.Fail("rate_limited");
        }
        catch (AnthropicBadRequestException ex)
        {
            _logger.LogError(ex, "Claude API rejected the request for {Feature}", request.Feature);
            return AiCallResult.Fail("bad_request");
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogError(ex, "Claude API error for {Feature}", request.Feature);
            return AiCallResult.Fail("api_error");
        }
        catch (AnthropicIOException ex)
        {
            _logger.LogWarning(ex, "Claude API connection error for {Feature}", request.Feature);
            return AiCallResult.Fail("network");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling the Claude API for {Feature}", request.Feature);
            return AiCallResult.Fail("error");
        }

        var usage = response.Usage;
        long input = usage.InputTokens, output = usage.OutputTokens;
        long cacheRead = usage.CacheReadInputTokens ?? 0, cacheWrite = usage.CacheCreationInputTokens ?? 0;

        if (response.StopReason == StopReason.Refusal)
        {
            var category = AiCallResult.SafeCode(response.StopDetails?.Category?.Raw());
            _logger.LogWarning("Claude declined the {Feature} request (category {Category})", request.Feature, category);
            return new AiCallResult(AiCallOutcome.Refused, null, input, output, cacheRead, cacheWrite, category is null ? "refusal" : "refusal:" + category);
        }
        if (response.StopReason == StopReason.MaxTokens)
        {
            _logger.LogWarning("Claude output for {Feature} hit max_tokens ({MaxTokens})", request.Feature, request.MaxTokens);
            return new AiCallResult(AiCallOutcome.MaxTokens, null, input, output, cacheRead, cacheWrite, "max_tokens");
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new AiCallResult(AiCallOutcome.Succeeded, json, input, output, cacheRead, cacheWrite);
    }

    private static Effort ParseEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.Low,
    };
}
