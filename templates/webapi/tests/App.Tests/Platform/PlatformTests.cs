// PLATFORM-OWNED (template-locked). Guards the platform guarantees; agents may not delete or edit these.
using System.Net;
using System.Net.Http.Json;
using App.Platform;
using App.Platform.Caching;
using App.Platform.Data;
using App.Platform.Ops;
using App.Platform.Queue;
using App.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace App.Tests.Platform;

public sealed class PlatformTests
{
    [Fact]
    public async Task Health_endpoints_report_live_and_ready()
    {
        await using var app = new TestApp();
        var client = app.CreateClient();

        (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Every_response_carries_instance_id_and_ops_stats_are_exposed()
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["InstanceId"] = "test-1" });
        var client = app.CreateClient();

        var response = await client.GetAsync("/ops/stats", TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Instance-Id").Single().ShouldBe("test-1");
        var json = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>(TestContext.Current.CancellationToken);
        json!.ShouldContainKey("cache");
    }

    [Fact]
    public async Task Rate_limiter_returns_429_when_client_exceeds_window()
    {
        await using var app = new TestApp(new Dictionary<string, string?> { ["RateLimiting:PermitLimit"] = "2", ["RateLimiting:WindowSeconds"] = "60" });
        var client = app.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        await client.GetAsync("/does-not-exist", ct);
        await client.GetAsync("/does-not-exist", ct);
        var third = await client.GetAsync("/does-not-exist", ct);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        app.Service<OpsCounters>().RateLimitedCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_failures_degrade_to_source_and_open_the_circuit()
    {
        var ops = new OpsCounters();
        var cache = new ResilientCacheService(new ThrowingCache(),
            Options.Create(new CacheOptions { CircuitBreaker = new() { MinimumThroughput = 2, FailureRatio = 0.5, SamplingSeconds = 30, BreakSeconds = 30 } }),
            Options.Create(new ChaosOptions()), ops);

        for (var i = 0; i < 5; i++)
        {
            var value = await cache.GetOrCreateAsync<string>("k", _ => Task.FromResult<string?>("from-db"), ct: TestContext.Current.CancellationToken);
            value.ShouldBe("from-db"); // request still served from the source of truth
        }

        ops.CacheCircuit.ShouldBe("Open");
        ops.CacheErrors.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Full_queue_drops_and_counts_instead_of_blocking()
    {
        var ops = new OpsCounters();
        var queue = new ChannelBackgroundQueue<int>(new QueueOptions<int> { Capacity = 2 }, ops);

        queue.TryEnqueue(1).ShouldBeTrue();
        queue.TryEnqueue(2).ShouldBeTrue();
        queue.TryEnqueue(3).ShouldBeFalse();

        ops.Dropped.ShouldBe(1);
        queue.Depth.ShouldBe(2);
    }

    [Fact]
    public async Task Batching_worker_flushes_batches_to_the_handler()
    {
        var services = new ServiceCollection();
        var handler = new RecordingHandler();
        services.AddSingleton<IBatchHandler<int>>(handler);
        var sp = services.BuildServiceProvider();
        var ops = new OpsCounters();
        var options = new QueueOptions<int>();
        var worker = new BatchingWorker<int>(new ChannelBackgroundQueue<int>(options, ops), options,
            sp.GetRequiredService<IServiceScopeFactory>(), new ResilientDbExecutor(), ops, NullLogger<BatchingWorker<int>>.Instance);

        await worker.FlushAsync([1, 2, 3], TestContext.Current.CancellationToken);

        handler.Received.ShouldBe([1, 2, 3]);
    }

    private sealed class ThrowingCache : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => throw new InvalidOperationException("cache down");
        public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) => throw new InvalidOperationException("cache down");
        public Task RemoveAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("cache down");
        public Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan? ttl = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("cache down");
    }

    private sealed class RecordingHandler : IBatchHandler<int>
    {
        public List<int> Received { get; } = [];
        public Task HandleAsync(IReadOnlyList<int> batch, CancellationToken ct) { Received.AddRange(batch); return Task.CompletedTask; }
    }
}
