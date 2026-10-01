# URL Shortener

HTTP API that creates short links for long URLs (optionally with a custom alias), redirects visitors to the original URL and reports per-link click analytics.

## Features
- **Create short links** — `POST /api/v1/urls` with an absolute http(s) URL; a 7-character base62 code is generated.
- **Custom aliases** — optional `customAlias` (4–32 chars, `[A-Za-z0-9_-]`); taken aliases return `409`, reserved words are rejected.
- **Redirects** — `GET /{code}` answers `302 Found` so every click reaches the service and is counted.
- **Click analytics** — total clicks, clicks per day and top referrers via `GET /api/v1/urls/{code}/stats?days=30`.
- **Privacy by design** — only the referrer host and user-agent family are stored; no IP addresses.

## API
| Method | Route | Description | Responses |
|---|---|---|---|
| POST | `/api/v1/urls` | Create a short link; optional expiresInMinutes (1-525600) | 201 UrlResponse incl. expiresAt, 400 invalid expiry/url/alias, 409 alias in use |
| GET | `/api/v1/urls/{code}` | Link metadata incl. expiresAt | 200 UrlResponse, 404 |
| GET | `/api/v1/urls/{code}/stats` | Click analytics (`days` = 1–365, default 30) | 200, 404 |
| GET | `/{code}` | Redirect; expired links are gone | 302 Location, 404, 410 Gone (expired) |

Example:
```http
POST http://localhost:5080/api/v1/urls
Content-Type: application/json

{ "url": "https://learn.microsoft.com/dotnet/", "customAlias": "dotnet-docs" }
```

## Changes in v2 — Optional link expiration
Callers can give a short link an optional lifetime; expired links stop redirecting (410 Gone) and metadata exposes the expiry time.

- **FR-7** POST /api/v1/urls accepts an optional expiresInMinutes; the link expires that many minutes after creation. _(acceptance: expiresInMinutes between 1 and 525600 (one year) is accepted; 0, negative or larger values return 400 problem details; omitting it creates a link that never expires.)_
- **FR-8** An expired link no longer redirects. _(acceptance: GET /{code} on an expired link returns 410 Gone (problem details) and records no click; before expiry it still returns 302. Expiry is enforced even when the link is cached.)_
- **FR-9** Link metadata shows when the link expires. _(acceptance: GET /api/v1/urls/{code} and the 201 create response include expiresAt (null when the link never expires).)_

## Running locally
```bash
dotnet run --project src/App          # http://localhost:5080
```
Platform endpoints: `GET /health/live`, `GET /health/ready`, `GET /ops/stats` (cache hit ratio, queue depth, dropped events, rate-limit rejections). Every response carries an `X-Instance-Id` header.

## Configuration
| Key | Default | Notes |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Postgres` is supported; set `Database:ConnectionString` |
| `Cache:Provider` | `Memory` | `Redis` shares the cache across instances (`Cache:RedisConfiguration`) |
| `RateLimiting:PermitLimit` / `WindowSeconds` | `200` / `10` | Fixed window per client |
| `Analytics:QueueCapacity` | `10000` | Click events beyond this are dropped (and counted), never blocking redirects |
| `Chaos:CacheFaultRate`, `Chaos:DbLatencyMs` | `0` | Fault injection for resilience demos only |

## Testing
```bash
dotnet test                                                     # unit + integration tests
dotnet test --coverlet --coverlet-output-format cobertura       # with coverage
```
Integration tests run the real app in memory (`WebApplicationFactory`) over a throw-away SQLite database.

## Design notes
- **Read path:** redirects resolve codes through a cache-aside lookup (`ICacheService`, 10-minute TTL); the database is only hit on a miss.
- **Write-behind analytics:** a redirect only enqueues a click; a background worker writes clicks in batches and increments a denormalised counter.
- **Scale-out:** instances are stateless and codes are random with a unique index and retry on collision, so several instances can run behind a load balancer.
- **Resilience:** cache failures degrade to the database through a circuit breaker; transient database errors are retried with backoff; per-client rate limiting and request timeouts protect the host.
- See `docs/ARCHITECTURE.md` for decisions (ADRs), NFR traceability and the capacity report.
