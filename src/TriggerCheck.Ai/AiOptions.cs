namespace TriggerCheck.Ai;

/// <summary>Configuration section "Ai". See docs/DEPLOYMENT.md (AI section).</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Global kill switch. Off by default; the product works fully without AI.</summary>
    public bool Enabled { get; set; }
    /// <summary>Claude API key. Prefer the ANTHROPIC_API_KEY environment variable; this setting wins when both are set.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Default model for every feature (decision K1: Opus 5.5). Per feature: Ai:Features:&lt;Name&gt;:Model.</summary>
    public string Model { get; set; } = DefaultModel;
    /// <summary>Default effort (low / medium / high / xhigh / max). Per feature: Ai:Features:&lt;Name&gt;:Effort.</summary>
    public string Effort { get; set; } = "low";
    /// <summary>Default output token cap per call.</summary>
    public int MaxTokens { get; set; } = 4000;
    /// <summary>Per-call timeout (the SDK's own retries happen inside it).</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>SDK retries for 408/409/429/5xx and connection errors (SDK default: 2).</summary>
    public int MaxRetries { get; set; } = 2;
    /// <summary>Jobs waiting in the in-process AI queue; further jobs are dropped (logged).</summary>
    public int QueueCapacity { get; set; } = 200;
    /// <summary>AI calls running in parallel (independent of the monitoring engine's concurrency).</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Per-feature overrides, keyed by <see cref="TriggerCheck.Domain.AiFeatures"/> name.</summary>
    public Dictionary<string, AiFeatureOptions> Features { get; set; } = new();

    /// <summary>USD prices per million tokens, keyed by model id (for cost estimates). Defaults cover the K1 models.</summary>
    public Dictionary<string, AiModelPrice> Pricing { get; set; } = new();

    public AiBaselineOptions Baseline { get; set; } = new();

    /// <summary>The key from Ai:ApiKey or ANTHROPIC_API_KEY (null when neither is set).</summary>
    public string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env ? env : null;

    public string ModelFor(string feature) => Features.TryGetValue(feature, out var f) && !string.IsNullOrWhiteSpace(f.Model) ? f.Model! : Model;
    public string EffortFor(string feature) => Features.TryGetValue(feature, out var f) && !string.IsNullOrWhiteSpace(f.Effort) ? f.Effort! : Effort;
    public int MaxTokensFor(string feature) => Features.TryGetValue(feature, out var f) && f.MaxTokens is > 0 ? f.MaxTokens.Value : MaxTokens;

    public AiModelPrice PriceFor(string model) =>
        Pricing.TryGetValue(model, out var p) ? p
        : DefaultPrices.TryGetValue(model, out var d) ? d
        : DefaultPrices[DefaultModel]; // unknown model: estimate conservatively with the default model's prices

    /// <summary>First-party Claude API list prices (USD / 1M tokens); cache writes are 1.25 x input (5-minute TTL).</summary>
    public static readonly IReadOnlyDictionary<string, AiModelPrice> DefaultPrices = new Dictionary<string, AiModelPrice>
    {
        ["claude-opus-5-5"] = new() { InputPerMTok = 4m, OutputPerMTok = 20m, CacheReadPerMTok = 0.20m, CacheWritePerMTok = 5m },
        ["claude-haiku-5-5"] = new() { InputPerMTok = 0.10m, OutputPerMTok = 0.50m, CacheReadPerMTok = 0.01m, CacheWritePerMTok = 0.125m },
    };
}

public sealed class AiFeatureOptions
{
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class AiModelPrice
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
    public decimal CacheReadPerMTok { get; set; }
    public decimal CacheWritePerMTok { get; set; }

    public decimal Estimate(long input, long output, long cacheRead, long cacheWrite) =>
        (input * InputPerMTok + output * OutputPerMTok + cacheRead * CacheReadPerMTok + cacheWrite * CacheWritePerMTok) / 1_000_000m;
}

/// <summary>"Ai:Baseline": the L1 statistical baseline job (AI-1). Runs without an LLM, independent of Ai:Enabled.</summary>
public sealed class AiBaselineOptions
{
    /// <summary>Runs only when Monitoring:Enabled is also true (no checks, nothing to learn).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How often anomalies are evaluated.</summary>
    public int IntervalMinutes { get; set; } = 5;
    /// <summary>Baselines older than this are recomputed from the window.</summary>
    public int RefreshMinutes { get; set; } = 60;
    public int WindowDays { get; set; } = 7;
    /// <summary>An hour bucket with fewer samples falls back to the global bucket.</summary>
    public int MinBucketSamples { get; set; } = 20;
    /// <summary>No anomaly evaluation until the global baseline has this many samples.</summary>
    public int MinGlobalSamples { get; set; } = 50;
    /// <summary>Robust z-score above which a check counts as anomalous.</summary>
    public double ZThreshold { get; set; } = 4.0;
    /// <summary>A check must also be at least this many times the baseline median (ignores tiny absolute deviations).</summary>
    public double MinRatio { get; set; } = 1.5;
    /// <summary>Anomaly when at least K of the last N successful checks are anomalous.</summary>
    public int K { get; set; } = 3;
    public int N { get; set; } = 5;
    /// <summary>Most recent successful checks read per monitor when building a baseline.</summary>
    public int MaxSamples { get; set; } = 50_000;
    /// <summary>Monitors processed per batch (one query per batch for the recent checks).</summary>
    public int BatchSize { get; set; } = 100;
}
