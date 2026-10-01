# NFR pattern catalog

The architect agent must map every non-functional concern to one of these patterns, name the platform building block
that implements it, and name the test that proves it. The validation gate enforces the matching static conventions.

| NFR signal | Pattern | Platform building block | Typical proof |
|---|---|---|---|
| Read-heavy path (reads ≫ writes) | Cache-aside with TTL | `ICacheService.GetOrCreateAsync` | Integration test: second read served without DB; `/ops/stats` hit ratio |
| Latency-sensitive request path with side work (analytics, notifications) | Asynchronous write-behind, batched | `IBackgroundQueue<T>` + `IBatchHandler<T>` | Test: request returns before the batch is written; stats eventually consistent |
| Bursty load / overload protection | Bounded queue with load shedding | `QueueOptions.Capacity` (drops + counts) | Test: full queue drops without blocking |
| Abuse / noisy neighbour | Rate limiting per client | Platform rate limiter (429 + Retry-After) | Platform test: N+1th request → 429 |
| Slow dependency | Timeout, fail fast | Request timeouts (504) | Chaos: `Chaos:DbLatencyMs` > timeout |
| Optional dependency outage (cache) | Circuit breaker + graceful degradation | `ResilientCacheService` | Test: cache throws → request still succeeds, circuit Open |
| Transient DB failures | Retry with exponential backoff + jitter | `IDbExecutor` | Unit test with transient exception |
| Horizontal scale-out | Stateless instances, shared state in DB/cache | No mutable statics (convention), Redis provider | Concurrency test across parallel requests; multi-instance demo |
| Unique identifiers across instances | Random IDs + unique constraint + retry on collision | DB unique index | Test: N parallel creates → N distinct ids |
| Input abuse / security | Validate at the edge, allow-lists | ProblemDetails validation | Unit tests for rejected inputs |
| Privacy / compliance | Data minimisation (store no raw PII) | Entity design | Review + schema inspection |
| Data growth | Retention / TTL, aggregate counters | Denormalised counters, retention setting | Capacity report storage check |
| Testable time-based behaviour | Injected clock | `TimeProvider` (`FakeTimeProvider` in tests) | Test advances the fake clock |
| Availability target ≥ 99.9% | Redundancy across nodes/zones | Deployment profile (kubernetes) | Feasibility gate + manifest checks (probes, PDB, HPA) |
