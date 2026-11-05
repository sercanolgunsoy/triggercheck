using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// Real tool-use turn against the Claude API (official Anthropic C# SDK): one non-streaming Messages call per turn with
/// the caller's tools (strict schemas), effort from config, adaptive thinking (the model default) and prompt caching
/// (a breakpoint on the static system prompt, which also covers the tool list, plus automatic caching of the growing
/// conversation). The assistant content of every turn, including thinking blocks and their signatures, is kept as
/// <see cref="AiToolMessage.ProviderContent"/> and echoed back unchanged (append-only history). Refusals and max_tokens
/// are reported, never parsed. Like <see cref="AnthropicAiClient"/>, no server-side model fallback is configured: model
/// changes go through the eval gate (AI-11). Logs never contain prompt, tool or response text (tenant data).
/// </summary>
public sealed class AnthropicAiToolClient : IAiToolClient
{
    private readonly AnthropicClient _client;
    private readonly ILogger<AnthropicAiToolClient> _logger;

    public AnthropicAiToolClient(IOptions<AiOptions> options, ILogger<AnthropicAiToolClient> logger)
    {
        var o = options.Value;
        _logger = logger;
        _client = new AnthropicClient
        {
            ApiKey = o.ResolveApiKey() ?? throw new InvalidOperationException("No Claude API key configured."),
            MaxRetries = Math.Max(0, o.MaxRetries),
            Timeout = AnthropicAiClient.AttemptTimeout(o),
        };
    }

    public bool IsAvailable => true;

    public async Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = request.SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            // Automatic caching of the conversation prefix: each turn of the loop re-reads the previous turns from cache.
            CacheControl = new CacheControlEphemeral(),
            Tools = request.Tools.Select(ToTool).ToList(),
            // "auto" (forced tool choice is not supported by current models); "none" makes the model answer now.
            ToolChoice = request.AllowToolUse ? new ToolChoiceAuto() : new ToolChoiceNone(),
            Messages = request.Messages.Select(ToParam).ToList(),
            OutputConfig = new OutputConfig { Effort = ParseEffort(request.Effort) },
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
            return AiToolTurn.Fail("timeout");
        }
        catch (AnthropicRateLimitException ex)
        {
            _logger.LogWarning(ex, "Claude API rate limit for {Feature}", request.Feature);
            return AiToolTurn.Fail("rate_limited");
        }
        catch (AnthropicBadRequestException ex)
        {
            _logger.LogError(ex, "Claude API rejected the request for {Feature}", request.Feature);
            return AiToolTurn.Fail("bad_request");
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogError(ex, "Claude API error for {Feature}", request.Feature);
            return AiToolTurn.Fail("api_error");
        }
        catch (AnthropicIOException ex)
        {
            _logger.LogWarning(ex, "Claude API connection error for {Feature}", request.Feature);
            return AiToolTurn.Fail("network");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling the Claude API for {Feature}", request.Feature);
            return AiToolTurn.Fail("error");
        }

        var usage = response.Usage;
        long input = usage.InputTokens, output = usage.OutputTokens;
        long cacheRead = usage.CacheReadInputTokens ?? 0, cacheWrite = usage.CacheCreationInputTokens ?? 0;

        if (response.StopReason == StopReason.Refusal)
        {
            var category = AiCallResult.SafeCode(response.StopDetails?.Category?.Raw());
            _logger.LogWarning("Claude declined the {Feature} request (category {Category})", request.Feature, category);
            return new AiToolTurn(AiCallOutcome.Refused, null, [], null, input, output, cacheRead, cacheWrite, category is null ? "refusal" : "refusal:" + category);
        }
        if (response.StopReason == StopReason.MaxTokens)
        {
            _logger.LogWarning("Claude output for {Feature} hit max_tokens ({MaxTokens})", request.Feature, request.MaxTokens);
            return new AiToolTurn(AiCallOutcome.MaxTokens, null, [], null, input, output, cacheRead, cacheWrite, "max_tokens");
        }

        // Echo the assistant turn back exactly (thinking blocks keep their signatures); collect text and tool calls.
        var echo = new List<ContentBlockParam>();
        var calls = new List<AiToolCall>();
        var text = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                echo.Add(new TextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out var thinking))
                echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (block.TryPickRedactedThinking(out var redacted))
                echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickToolUse(out var toolUse))
            {
                echo.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                calls.Add(new AiToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            }
            // No server tools are declared, so no other block types are expected.
        }

        var assistant = new AiToolMessage(AiToolRole.Assistant, text.ToString(), calls, ProviderContent: echo);
        return new AiToolTurn(AiCallOutcome.Succeeded, text.ToString(), calls, assistant, input, output, cacheRead, cacheWrite);
    }

    private static ToolUnion ToTool(AiToolDefinition d)
    {
        var schema = d.InputSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return new Tool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = InputSchema.FromRawUnchecked(schema),
            Strict = true,
        };
    }

    private static MessageParam ToParam(AiToolMessage m)
    {
        if (m.Role == AiToolRole.Assistant)
        {
            if (m.ProviderContent is List<ContentBlockParam> echo) return new AssistantMessageParam { Content = echo };
            var blocks = new List<ContentBlockParam>();
            if (!string.IsNullOrEmpty(m.Text)) blocks.Add(new TextBlockParam { Text = m.Text });
            foreach (var c in m.ToolCalls ?? [])
                blocks.Add(new ToolUseBlockParam
                {
                    ID = c.Id, Name = c.Name,
                    Input = c.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
                });
            return new AssistantMessageParam { Content = blocks };
        }
        if (m.ToolResults is { Count: > 0 } results)
        {
            List<ContentBlockParam> blocks = results
                .Select(r => (ContentBlockParam)new ToolResultBlockParam { ToolUseID = r.ToolCallId, Content = r.Content, IsError = r.IsError })
                .ToList();
            return new UserMessageParam { Content = blocks };
        }
        return new UserMessageParam { Content = m.Text ?? "" };
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
