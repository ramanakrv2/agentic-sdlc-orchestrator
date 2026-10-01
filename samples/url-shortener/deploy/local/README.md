# Local deployment — url-shortener

Single machine, SQLite (WAL) + in-memory cache.

Limits: SQLite has a single writer; each instance has its own in-memory cache (use Cache:Provider=Redis to share).
