# NFR traceability

| NFR | Pattern | Building block | Verified by | Status |
|---|---|---|---|---|
| Read-heavy redirects (~100 reads per write), p95 latency | Cache-aside with TTL | ICacheService.GetOrCreateAsync in UrlService.ResolveAsync | UrlApiTests.Redirect_returns_302_to_target_and_unknown_code_returns_404; /ops/stats hit ratio under LoadGen | ✅ validation gate passed |
| Analytics must not slow redirects | Asynchronous write-behind, batched | IBackgroundQueue<ClickEvent> + ClickRecorder (IBatchHandler) | UrlApiTests.Clicks_are_recorded_asynchronously_and_reported_in_stats | ✅ validation gate passed |
| Burst protection for analytics | Bounded queue with load shedding | QueueOptions.Capacity (Analytics:QueueCapacity) | PlatformTests.Full_queue_drops_and_counts_instead_of_blocking | ✅ validation gate passed |
| Horizontal scale-out / multiple instances | Stateless instances; random IDs + unique constraint + retry on collision | Base62CodeGenerator + unique index + UrlService retry loop | UrlApiTests.Parallel_creates_never_produce_duplicate_codes | ✅ validation gate passed |
| Cache outage | Circuit breaker + graceful degradation | ResilientCacheService (platform) | PlatformTests.Cache_failures_degrade_to_source_and_open_the_circuit | ✅ validation gate passed |
| Abuse of anonymous link creation | Rate limiting per client + validate at the edge | Platform rate limiter; UrlValidator | PlatformTests.Rate_limiter_returns_429_when_client_exceeds_window; UrlUnitTests.Invalid_urls_are_rejected | ✅ validation gate passed |
| Privacy (analytics) | Data minimisation | ClickEvent stores referrer host + UA family only | UrlUnitTests.User_agent_family_is_classified; schema review | ✅ validation gate passed |
| Data growth (click events) | Aggregate counters + bounded stats window | ShortUrl.ClickCount; stats window 1-365 days | Capacity report storage check | ✅ validation gate passed |
| Transient SQLite busy/locked | Retry with backoff + jitter | IDbExecutor in EfUrlRepository and BatchingWorker | Platform IDbExecutor transient classification | ✅ validation gate passed |
