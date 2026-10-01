# Capacity & Feasibility Report — profile `local`

**Verdict: ✅ Feasible**

## Derived load
- Requests/day: 1,000,000 → average **11.6 RPS**, peak (×10) **116 RPS**
- Read:write ratio 100:1 → peak writes **1.1 /s**
- Storage over 90 days retention: **18.3 GB**
- Availability target: **99%**

## Checks against the profile envelope
| Dimension | Required | Profile limit | Verdict | Note |
|---|---|---|---|---|
| Peak throughput | 115.7 RPS | 400.0 RPS | ✅ | 29% of envelope. Single host; cannot scale out. |
| Peak writes | 1.1 writes/s | 60.0 writes/s | ✅ | 2% of envelope. Write path is the usual bottleneck (single-writer DB on local). |
| Storage | 18.3 GB | 50.0 GB | ✅ | 37% of envelope. Retention × daily growth; consider TTL/archival of click events. |
| Availability | 99.0 % | 99.0 % | ✅ | Within what the profile can deliver. |

_Envelope basis: Measured on a 4-core laptop: SQLite WAL (single writer), in-memory cache, no redundancy._

## Assumptions
- Read:write ratio 100:1 (typical for read-heavy services).
- Data retention 90 days.
- Peak traffic = 10× the daily average.

## Strategies for this profile
- Stateless API instances behind the YARP gateway (scripts/run-cluster.ps1) to demonstrate scale-out on one host.
- Cache-aside on the read path (ICacheService) — redirects rarely touch SQLite.
- Analytics written off the request path in batches (bounded queue with load shedding).
- SQLite WAL: many concurrent readers, one writer — the write path is the ceiling.
- Rate limiting per client and request timeouts protect the single host.
