using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Orchestrator.Core.Llm;

namespace Orchestrator.Infrastructure.Llm;

public sealed record ChatCompletionResult(string Content, int PromptTokens, int CompletionTokens, double DurationMs);

/// <summary>Raw model call (no resilience). Seam for tests and for swapping providers.</summary>
public interface IChatModel
{
    Task<ChatCompletionResult> CompleteAsync(string model, LlmRequest request, CancellationToken ct);
}

/// <summary>
/// Semantic Kernel chat completion against Ollama's OpenAI-compatible endpoint.
/// Pointing <see cref="LlmOptions.Endpoint"/> at OpenAI/Azure OpenAI/vLLM needs no code change.
/// </summary>
public sealed class SemanticKernelChatModel(LlmOptions options) : IChatModel
{
    // Timeouts are owned by the Polly pipeline, not HttpClient.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, IChatCompletionService> _services = new();

    public async Task<ChatCompletionResult> CompleteAsync(string model, LlmRequest request, CancellationToken ct)
    {
        var service = _services.GetOrAdd(model, m =>
            new OpenAIChatCompletionService(modelId: m, endpoint: new Uri(options.Endpoint), apiKey: options.ApiKey, httpClient: _http));

        var history = new ChatHistory();
        history.AddSystemMessage(request.SystemPrompt);
        history.AddUserMessage(request.UserPrompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
        };
        if (request.JsonOutput) settings.ResponseFormat = "json_object";

        var sw = Stopwatch.StartNew();
        var message = await service.GetChatMessageContentAsync(history, settings, kernel: null, cancellationToken: ct);
        sw.Stop();

        int promptTokens = 0, completionTokens = 0;
        if (message.Metadata?.TryGetValue("Usage", out var usage) == true && usage is OpenAI.Chat.ChatTokenUsage u)
        {
            promptTokens = u.InputTokenCount;
            completionTokens = u.OutputTokenCount;
        }
        return new ChatCompletionResult(message.Content ?? "", promptTokens, completionTokens, sw.Elapsed.TotalMilliseconds);
    }
}
