# URL Shortener

Callers can give a short link an optional lifetime; expired links stop redirecting (410 Gone) and metadata exposes the expiry time.

## Features
- **FR-7** POST /api/v1/urls accepts an optional expiresInMinutes; the link expires that many minutes after creation.
- **FR-8** An expired link no longer redirects.
- **FR-9** Link metadata shows when the link expires.

## API
| Method | Route | Description |
|---|---|---|
| POST | `/api/v1/urls` | Create a short link; optional expiresInMinutes (1-525600) |
| GET | `/api/v1/urls/{code}` | Link metadata incl. expiresAt |
| GET | `/{code}` | Redirect; expired links are gone |

## Run locally
```
dotnet run --project src/App        # http://localhost:5080
dotnet test
```

## Configuration
| Key | Default | Notes |
|---|---|---|
| `Database:Provider` | `Sqlite` | `Postgres` supported (set `Database:ConnectionString`) |
| `Cache:Provider` | `Memory` | `Redis` (set `Cache:RedisConfiguration`) |
| `RateLimiting:PermitLimit` / `WindowSeconds` | 200 / 10 | per client |

Deployment profile: **local** — see `deploy/` and `docs/ARCHITECTURE.md`.
