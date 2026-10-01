using System.Text.Json;
using App.Platform.Ops;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Simmy;

namespace App.Platform.Caching;

// PLATFORM-OWNED (template-locked).

/// <summary>
/// Cache abstraction for features (cache-aside). Backed by <see cref="IDistributedCache"/>:
/// in-memory by default, Redis via configuration (Cache:Provider=Redis) with no code change.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    /// <summary>Returns the cached value or computes, caches and returns it. Null results are not cached.</summary>
    Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan? ttl = null, CancellationToken ct = default);
}

public sealed class DistributedCacheService(IDistributedCache cache, IOptions<CacheOptions> options) : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(key, ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, Json);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) =>
        cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, Json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromSeconds(options.Value.DefaultTtlSeconds) }, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(key, ct);

    public async Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null) return cached;
        var value = await factory(ct);
        if (value is not null) await SetAsync(key, value, ttl, ct);
        return value;
    }
}

/// <summary>
/// Graceful degradation: cache failures never fail a request. A circuit breaker stops calling an unhealthy cache
/// (e.g. Redis down) and requests fall through to the source of truth until it recovers.
/// </summary>
public sealed class ResilientCacheService : ICacheService
{
    private readonly ICacheService _inner;
    private readonly OpsCounters _ops;
    private readonly ResiliencePipeline _pipeline;
    private readonly bool _enabled;

    public ResilientCacheService(ICacheService inner, IOptions<CacheOptions> options, IOptions<ChaosOptions> chaos, OpsCounters ops)
    {
        _inner = inner;
        _ops = ops;
        _enabled = options.Value.Enabled;
        var cb = options.Value.CircuitBreaker;
        var builder = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = cb.FailureRatio,
                MinimumThroughput = Math.Max(2, cb.MinimumThroughput),
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, cb.SamplingSeconds)),
                BreakDuration = TimeSpan.FromSeconds(Math.Max(1, cb.BreakSeconds)),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                OnOpened = _ => { ops.CacheCircuit = "Open"; return ValueTask.CompletedTask; },
                OnHalfOpened = _ => { ops.CacheCircuit = "HalfOpen"; return ValueTask.CompletedTask; },
                OnClosed = _ => { ops.CacheCircuit = "Closed"; return ValueTask.CompletedTask; },
            });
        if (chaos.Value.CacheFaultRate > 0)
            builder.AddChaosFault(chaos.Value.CacheFaultRate, () => new InvalidOperationException("Injected cache fault (chaos)"));
        _pipeline = builder.Build();
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (!_enabled) return default;
        try
        {
            var value = await _pipeline.ExecuteAsync(async t => await _inner.GetAsync<T>(key, t), ct);
            if (value is null) _ops.CacheMiss(); else _ops.CacheHit();
            return value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ops.CacheError();
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (!_enabled) return;
        try { await _pipeline.ExecuteAsync(async t => await _inner.SetAsync(key, value, ttl, t), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _ops.CacheError(); }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        if (!_enabled) return;
        try { await _pipeline.ExecuteAsync(async t => await _inner.RemoveAsync(key, t), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _ops.CacheError(); }
    }

    public async Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null) return cached;
        var value = await factory(ct);
        if (value is not null) await SetAsync(key, value, ttl, ct);
        return value;
    }
}

public static class CacheSetup
{
    public static IServiceCollection AddPlatformCache(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CacheOptions.Section).Get<CacheOptions>() ?? new CacheOptions();
        if (options.Provider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
            services.AddStackExchangeRedisCache(o => o.Configuration = options.RedisConfiguration);
        else
            services.AddDistributedMemoryCache();

        services.AddSingleton<DistributedCacheService>();
        services.AddSingleton<ICacheService>(sp => new ResilientCacheService(
            sp.GetRequiredService<DistributedCacheService>(),
            sp.GetRequiredService<IOptions<CacheOptions>>(),
            sp.GetRequiredService<IOptions<ChaosOptions>>(),
            sp.GetRequiredService<OpsCounters>()));
        return services;
    }
}
