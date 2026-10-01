using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Orchestrator.Core.Llm;
using Orchestrator.Core.Persistence;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Simmy;
using Polly.Timeout;

namespace Orchestrator.Infrastructure.Llm;

public sealed class ChaosFaultException(string message) : Exception(message);

/// <summary>
/// Transient-failure handling for LLM calls (distinct from the engine's semantic retries):
/// <code>
/// per model:  Retry(exp backoff + jitter) → CircuitBreaker → Timeout → [Chaos] → model call
/// chain:      primary model → fallback model → replay cache (auto mode) → LlmUnavailableException
/// </code>
/// An open circuit short-circuits straight to the next link instead of waiting for timeouts.
/// </summary>
public sealed class ResilientLlmClient : ILlmClient
{
    private static readonly ResiliencePropertyKey<LlmRequest> RequestKey = new("llm.request");

    private readonly IChatModel _model;
    private readonly ReplayCache _cache;
    private readonly LlmOptions _options;
    private readonly IEventStore _events;
    private readonly TimeProvider _time;
    private readonly ILogger<ResilientLlmClient> _logger;
    private readonly HttpClient? _probeClient;
    private readonly List<Orchestrator.Core.Engine.IRunObserver> _observers;
    private readonly SemaphoreSlim _concurrency;
    private readonly List<string> _models;
    private readonly Dictionary<string, ResiliencePipeline<ChatCompletionResult>> _pipelines;
    private DateTimeOffset _liveCheckedAt = DateTimeOffset.MinValue;
    private bool _liveUp;

    public ResilientLlmClient(IChatModel model, ReplayCache cache, LlmOptions options, IEventStore events, TimeProvider time,
        ILogger<ResilientLlmClient> logger, HttpClient? probeClient = null, IEnumerable<Orchestrator.Core.Engine.IRunObserver>? observers = null)
    {
        _model = model;
        _cache = cache;
        _options = options;
        _events = events;
        _time = time;
        _logger = logger;
        _probeClient = probeClient;
        _observers = observers?.ToList() ?? [];
        _concurrency = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentCalls));
        _models = new[] { options.PrimaryModel, options.FallbackModel }
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct().ToList();
        _pipelines = _models.ToDictionary(m => m, BuildPipeline);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var key = ReplayCache.KeyFor(request);

        if (_options.Mode == LlmMode.Replay || request.PreferReplay)
        {
            if (TryReplay(request, out var replayed)) return await EmitCallAsync(request, replayed!);
            if (_options.Mode == LlmMode.Replay || request.PreferReplay)
            {
                var pending = _cache.WritePending(request);
                throw new LlmUnavailableException($"No replay entry for agent '{request.Agent}' (key {key}). Prompt saved to {pending}.");
            }
        }

        Exception? last = null;
        if (await IsLiveAvailableAsync(ct))
        {
            for (var i = 0; i < _models.Count; i++)
            {
                var model = _models[i];
                try
                {
                    var result = await ExecuteAsync(model, request, ct);
                    var response = new LlmResponse(result.Content, model, i == 0 ? LlmSource.Live : LlmSource.FallbackModel,
                        result.PromptTokens, result.CompletionTokens, result.DurationMs, key);
                    if (i > 0) await EmitAsync(request, EventTypes.LlmModelFallback, null, new() { ["from"] = _models[0], ["to"] = model, ["reason"] = last?.GetType().Name ?? "" });
                    if (_options.RecordLiveResponses) _cache.Record(request, response);
                    return await EmitCallAsync(request, response);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    last = ex;
                    _logger.LogWarning("LLM model {Model} failed for agent {Agent}: {Error}", model, request.Agent, ex.Message);
                }
            }
        }
        else
        {
            last = new LlmUnavailableException($"LLM endpoint {_options.Endpoint} is not reachable.");
        }

        if (_options.Mode == LlmMode.Auto && TryReplay(request, out var fallback))
        {
            await EmitAsync(request, EventTypes.LlmModelFallback, null, new() { ["from"] = "live", ["to"] = "replay", ["reason"] = last?.Message ?? "" });
            return await EmitCallAsync(request, fallback!);
        }

        var file = _cache.WritePending(request);
        throw new LlmUnavailableException($"All LLM sources failed for agent '{request.Agent}': {last?.Message}. Prompt saved to {file}.", last);
    }

    private async Task<ChatCompletionResult> ExecuteAsync(string model, LlmRequest request, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        var context = ResilienceContextPool.Shared.Get(ct);
        try
        {
            context.Properties.Set(RequestKey, request);
            return await _pipelines[model].ExecuteAsync(
                static async (ctx, state) => await state.Model.CompleteAsync(state.Name, state.Request, ctx.CancellationToken),
                context,
                (Model: _model, Name: model, Request: request));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
            _concurrency.Release();
        }
    }

    private ResiliencePipeline<ChatCompletionResult> BuildPipeline(string model)
    {
        var handle = new PredicateBuilder<ChatCompletionResult>().Handle<Exception>(IsTransient);
        var cb = _options.CircuitBreaker;

        var builder = new ResiliencePipelineBuilder<ChatCompletionResult>();
        if (_options.MaxRetries > 0)
            builder.AddRetry(new RetryStrategyOptions<ChatCompletionResult>
            {
                MaxRetryAttempts = _options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = handle,
                OnRetry = args => new ValueTask(EmitAsync(Request(args.Context), EventTypes.LlmTransientRetry, null, new()
                {
                    ["model"] = model, ["attempt"] = (args.AttemptNumber + 1).ToString(), ["error"] = args.Outcome.Exception?.Message ?? "",
                })),
            });

        builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<ChatCompletionResult>
            {
                FailureRatio = cb.FailureRatio,
                MinimumThroughput = Math.Max(2, cb.MinimumThroughput),
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, cb.SamplingSeconds)),
                BreakDuration = TimeSpan.FromSeconds(Math.Max(1, cb.BreakSeconds)),
                ShouldHandle = handle,
                OnOpened = args => new ValueTask(EmitAsync(Request(args.Context), EventTypes.CircuitOpened, null, new()
                {
                    ["model"] = model, ["breakSeconds"] = args.BreakDuration.TotalSeconds.ToString(),
                })),
                OnClosed = args => new ValueTask(EmitAsync(Request(args.Context), EventTypes.CircuitClosed, null, new() { ["model"] = model })),
            })
            .AddTimeout(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));

        if (_options.Chaos.LatencyRate > 0)
            builder.AddChaosLatency(_options.Chaos.LatencyRate, TimeSpan.FromMilliseconds(_options.Chaos.LatencyMs));
        if (_options.Chaos.FaultRate > 0)
            builder.AddChaosFault(_options.Chaos.FaultRate, () => new ChaosFaultException($"Injected fault (chaos rate {_options.Chaos.FaultRate:P0})"));

        return builder.Build();
    }

    public static bool IsTransient(Exception ex) => ex switch
    {
        ChaosFaultException => true,
        TimeoutRejectedException => true,
        HttpRequestException => true,
        IOException => true,
        HttpOperationException h => h.StatusCode is null || (int)h.StatusCode >= 500 || (int)h.StatusCode == 429,
        _ => ex.InnerException is not null && IsTransient(ex.InnerException),
    };

    private async Task<bool> IsLiveAvailableAsync(CancellationToken ct)
    {
        if (_options.Mode == LlmMode.Live || _probeClient is null) return true;
        var now = _time.GetUtcNow();
        if (now - _liveCheckedAt < TimeSpan.FromSeconds(60)) return _liveUp;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _probeClient.GetAsync(_options.Endpoint.TrimEnd('/') + "/models", cts.Token);
            _liveUp = resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("LLM endpoint probe failed: {Error}", ex.Message);
            _liveUp = false;
        }
        _liveCheckedAt = now;
        return _liveUp;
    }

    private bool TryReplay(LlmRequest request, out LlmResponse? response)
    {
        var content = _cache.TryGet(request, out var key, out var tier);
        response = content is null ? null : new LlmResponse(content, $"replay:{tier}", LlmSource.Replay, 0, 0, 0, key);
        return response is not null;
    }

    private async Task<LlmResponse> EmitCallAsync(LlmRequest request, LlmResponse response)
    {
        await EmitAsync(request, EventTypes.LlmCall, response.DurationMs, new()
        {
            ["agent"] = request.Agent, ["model"] = response.Model, ["source"] = response.Source.ToString(),
            ["tokens"] = (response.PromptTokens + response.CompletionTokens).ToString(), ["key"] = response.CacheKey,
        });
        return response;
    }

    private static LlmRequest? Request(ResilienceContext ctx) => ctx.Properties.TryGetValue(RequestKey, out var r) ? r : null;

    private async Task EmitAsync(LlmRequest? request, string type, double? durationMs, Dictionary<string, string> data)
    {
        try
        {
            if (request?.Agent is { } agent) data.TryAdd("agent", agent);
            var evt = new RunEvent(request?.RunId ?? "-", type, _time.GetUtcNow(), request?.NodeId, null, durationMs, data);
            await _events.AppendAsync(evt);
            foreach (var o in _observers) o.OnEvent(evt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record LLM event");
        }
    }
}
