using System.Diagnostics;
using App.Platform.Queue;

namespace App.Platform.Ops;

// PLATFORM-OWNED (template-locked).

/// <summary>Process-local operational counters exposed at GET /ops/stats (demo-friendly; production would export OpenTelemetry metrics).</summary>
public sealed class OpsCounters
{
    private long _requests, _cacheHits, _cacheMisses, _cacheErrors, _queueDropped, _batches, _batchItems, _batchFailures, _rateLimited;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public string CacheCircuit { get; set; } = "Closed";

    public void Request() => Interlocked.Increment(ref _requests);
    public void CacheHit() => Interlocked.Increment(ref _cacheHits);
    public void CacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public void CacheError() => Interlocked.Increment(ref _cacheErrors);
    public void QueueDropped() => Interlocked.Increment(ref _queueDropped);
    public void RateLimited() => Interlocked.Increment(ref _rateLimited);
    public void BatchWritten(int items) { Interlocked.Increment(ref _batches); Interlocked.Add(ref _batchItems, items); }
    public void BatchFailed(int items) => Interlocked.Add(ref _batchFailures, items);

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long CacheErrors => Interlocked.Read(ref _cacheErrors);
    public long Dropped => Interlocked.Read(ref _queueDropped);
    public long RateLimitedCount => Interlocked.Read(ref _rateLimited);

    public object Snapshot(string instanceId, IEnumerable<IQueueProbe> queues)
    {
        var hits = CacheHits;
        var lookups = hits + CacheMisses;
        return new
        {
            instanceId,
            uptimeSeconds = (long)_uptime.Elapsed.TotalSeconds,
            requests = Interlocked.Read(ref _requests),
            rateLimited = RateLimitedCount,
            cache = new { hits, misses = CacheMisses, errors = CacheErrors, hitRatio = lookups == 0 ? 0 : Math.Round((double)hits / lookups, 3), circuit = CacheCircuit },
            queues = queues.Select(q => new { q.Name, q.Depth }).ToList(),
            background = new
            {
                dropped = Dropped,
                batchesWritten = Interlocked.Read(ref _batches),
                itemsWritten = Interlocked.Read(ref _batchItems),
                itemsFailed = Interlocked.Read(ref _batchFailures),
            },
        };
    }
}
