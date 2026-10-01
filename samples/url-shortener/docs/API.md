# API

Base URL (local): `http://localhost:5080`

| Method | Route | Description | Responses |
|---|---|---|---|
| POST | `/api/v1/urls` | Create a short link; optional expiresInMinutes (1-525600) | 201 UrlResponse incl. expiresAt<br>400 invalid expiry/url/alias<br>409 alias in use |
| GET | `/api/v1/urls/{code}` | Link metadata incl. expiresAt | 200 UrlResponse<br>404 |
| GET | `/{code}` | Redirect; expired links are gone | 302 Location<br>404<br>410 Gone (expired) |

Platform endpoints: `GET /health/live`, `GET /health/ready`, `GET /ops/stats`. Errors use RFC 7807 problem details; throttled requests get `429` with `Retry-After`.
