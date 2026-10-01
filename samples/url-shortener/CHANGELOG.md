# Changelog

## v2 — 2026-10-01 (brownfield)
Optional link expiration
- POST /api/v1/urls accepts an optional expiresInMinutes; the link expires that many minutes after creation.
- An expired link no longer redirects.
- Link metadata shows when the link expires.

## v1 — 2026-10-01 (greenfield)
URL Shortener with click analytics
- Create a short link for an absolute http/https URL; the service generates a unique short code.
- Optionally choose a custom alias instead of a generated code.
- Visiting a short link redirects to the original URL.
- Record every redirect as a click (timestamp, referrer host, user-agent family) without slowing the redirect.
- Report click analytics per link: total clicks, clicks per day and top referrers.
- Look up a short link's metadata.

