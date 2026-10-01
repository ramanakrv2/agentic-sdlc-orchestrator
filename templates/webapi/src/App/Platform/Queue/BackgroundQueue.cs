using System.Threading.Channels;
using App.Platform.Data;
using App.Platform.Ops;

namespace App.Platform.Queue;

// PLATFORM-OWNED (template-locked).

/// <summary>
/// Fire-and-forget work off the request path (e.g. analytics). Bounded: when full, items are dropped and counted
/// (load shedding) so producers never block. In-process today; a broker (RabbitMQ/Kafka) can implement the same interface.
/// </summary>
public interface IBackgroundQueue<in T>
{
    /// <returns>False when the queue is full and the item was dropped.</returns>
    bool TryEnqueue(T item);
}

/// <summary>Consumes a batch of queued items (e.g. one INSERT for many click events).</summary>
public interface IBatchHandler<in T>
{
    Task HandleAsync(IReadOnlyList<T> batch, CancellationToken ct);
}

public sealed class QueueOptions<T>
{
    public int Capacity { get; set; } = 10_000;
    public int BatchSize { get; set; } = 100;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

public sealed class ChannelBackgroundQueue<T>(QueueOptions<T> options, OpsCounters ops) : IBackgroundQueue<T>
{
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(options.Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full → we count and drop
        SingleReader = true,
        SingleWriter = false,
    });

    public ChannelReader<T> Reader => _channel.Reader;

    public int Depth => _channel.Reader.Count;

    public bool TryEnqueue(T item)
    {
        if (_channel.Writer.TryWrite(item)) return true;
        ops.QueueDropped();
        return false;
    }
}

/// <summary>Drains the queue in batches (size or interval, whichever first) and retries transient DB failures.</summary>
public sealed class BatchingWorker<T>(
    ChannelBackgroundQueue<T> queue,
    QueueOptions<T> options,
    IServiceScopeFactory scopes,
    IDbExecutor executor,
    OpsCounters ops,
    ILogger<BatchingWorker<T>> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<T>(options.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var flushTimer = new CancellationTokenSource(options.FlushInterval);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, flushTimer.Token);
                while (batch.Count < options.BatchSize && await queue.Reader.WaitToReadAsync(linked.Token))
                    while (batch.Count < options.BatchSize && queue.Reader.TryRead(out var item)) batch.Add(item);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // flush interval elapsed
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (batch.Count > 0) await FlushAsync(batch, stoppingToken);
        }

        // Drain what is left on shutdown (graceful stop).
        while (queue.Reader.TryRead(out var item)) batch.Add(item);
        if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None);
    }

    /// <summary>Flushes once; exposed for deterministic tests.</summary>
    public async Task FlushAsync(List<T> batch, CancellationToken ct)
    {
        try
        {
            await executor.ExecuteAsync(async token =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBatchHandler<T>>().HandleAsync(batch, token);
            }, ct);
            ops.BatchWritten(batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ops.BatchFailed(batch.Count);
            logger.LogError(ex, "Dropping batch of {Count} {Type} after retries", batch.Count, typeof(T).Name);
        }
        finally
        {
            batch.Clear();
        }
    }
}

public static class QueueSetup
{
    /// <summary>Registers a bounded queue for <typeparamref name="T"/> drained in batches by <typeparamref name="THandler"/>.</summary>
    public static IServiceCollection AddBackgroundQueue<T, THandler>(this IServiceCollection services, Action<QueueOptions<T>>? configure = null)
        where THandler : class, IBatchHandler<T>
    {
        var options = new QueueOptions<T>();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ChannelBackgroundQueue<T>>();
        services.AddSingleton<IBackgroundQueue<T>>(sp => sp.GetRequiredService<ChannelBackgroundQueue<T>>());
        services.AddScoped<IBatchHandler<T>, THandler>();
        services.AddSingleton<BatchingWorker<T>>();
        services.AddHostedService(sp => sp.GetRequiredService<BatchingWorker<T>>());
        services.AddSingleton<IQueueProbe>(sp => new QueueProbe(typeof(T).Name, () => sp.GetRequiredService<ChannelBackgroundQueue<T>>().Depth));
        return services;
    }
}

public interface IQueueProbe
{
    string Name { get; }
    int Depth { get; }
}

internal sealed class QueueProbe(string name, Func<int> depth) : IQueueProbe
{
    public string Name => name;
    public int Depth => depth();
}
