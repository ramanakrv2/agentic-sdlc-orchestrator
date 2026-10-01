# NFR traceability

| NFR | Pattern | Building block | Verified by | Status |
|---|---|---|---|---|
| Redirect latency must not regress (hot path) | Cache-aside storing target + expiry; expiry evaluated in memory | ICacheService with CachedLink value | UrlExpiryTests.Link_redirects_before_expiry_and_returns_410_after (cache hit after expiry still returns 410) | ✅ validation gate passed |
| Testable time-based behaviour | Injected clock | TimeProvider (FakeTimeProvider in tests) | UrlExpiryTests advances app.Clock | ✅ validation gate passed |
| Input abuse | Validate at the edge | UrlValidator (expiresInMinutes range) | UrlExpiryTests.Out_of_range_expiry_is_rejected / Invalid_expiry_returns_400 | ✅ validation gate passed |
| Backward compatibility | Additive, nullable schema and contract changes | Nullable ExpiresAt column, optional request/response fields | UrlExpiryTests.Links_without_expiry_never_expire + existing UrlApiTests unchanged | ✅ validation gate passed |
