# API

Base URL (local): `http://localhost:5080`

| Method | Route | Description | Responses |
|---|---|---|---|
| POST | `/api/v1/urls` | Create a short link; optional expiresInMinutes (1-525600) | 201 UrlResponse incl. expiresAt, 400 invalid expiry/url/alias, 409 alias in use |
| GET | `/api/v1/urls/{code}` | Link metadata incl. expiresAt | 200 UrlResponse, 404 |
| GET | `/api/v1/urls/{code}/stats` | Click analytics (total, per day, top referrers); ?days=1..365, default 30 | 200 UrlStatsResponse<br>404 |
| GET | `/{code}` | Redirect; expired links are gone | 302 Location, 404, 410 Gone (expired) |

Platform endpoints: `GET /health/live`, `GET /health/ready`, `GET /ops/stats`. Errors use RFC 7807 problem details; throttled requests get `429` with `Retry-After`.
