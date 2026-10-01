namespace Orchestrator.Core.Llm;

public enum LlmSource { Live, FallbackModel, Replay }

public sealed record LlmRequest
{
    /// <summary>Logical agent name (analyst, architect, coder...). Part of the replay key.</summary>
    public required string Agent { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public bool JsonOutput { get; init; }
    public double Temperature { get; init; } = 0.1;
    public int MaxTokens { get; init; } = 2048;
    public string? RunId { get; init; }
    public string? NodeId { get; init; }
    /// <summary>Forces the curated/recorded replay source (node-level fallback strategy).</summary>
    public bool PreferReplay { get; init; }
}

public sealed record LlmResponse(
    string Content,
    string Model,
    LlmSource Source,
    int PromptTokens,
    int CompletionTokens,
    double DurationMs,
    string CacheKey);

/// <summary>
/// Provider-agnostic LLM access. Implementation chain: primary model → fallback model → replay cache,
/// each live model wrapped in timeout → retry → circuit breaker.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
