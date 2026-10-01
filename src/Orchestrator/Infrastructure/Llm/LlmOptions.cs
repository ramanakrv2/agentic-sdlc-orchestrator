namespace Orchestrator.Infrastructure.Llm;

public enum LlmMode
{
    /// <summary>Live models first (primary → fallback model); replay cache as the last fallback.</summary>
    Auto,
    /// <summary>Live models only.</summary>
    Live,
    /// <summary>Replay cache only: deterministic, fast, no Ollama required.</summary>
    Replay,
}

public sealed class LlmOptions
{
    public LlmMode Mode { get; set; } = LlmMode.Auto;
    /// <summary>Ollama's OpenAI-compatible endpoint; any OpenAI-compatible server works.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string ApiKey { get; set; } = "ollama";
    public string PrimaryModel { get; set; } = "qwen2.5-coder:3b";
    public string? FallbackModel { get; set; } = "qwen2.5-coder:1.5b";
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; } = 2;
    public int MaxConcurrentCalls { get; set; } = 1;
    public string CacheDirectory { get; set; } = "llm-cache";
    public bool RecordLiveResponses { get; set; } = true;
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();
    public ChaosSettings Chaos { get; set; } = new();
}

public sealed class CircuitBreakerSettings
{
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 4;
    public int SamplingSeconds { get; set; } = 120;
    public int BreakSeconds { get; set; } = 30;
}

/// <summary>Fault injection (Polly chaos strategies) to demonstrate retries, circuit breaking and fallback.</summary>
public sealed class ChaosSettings
{
    public double FaultRate { get; set; }
    public double LatencyRate { get; set; }
    public int LatencyMs { get; set; } = 2000;
    public bool Enabled => FaultRate > 0 || LatencyRate > 0;
}
