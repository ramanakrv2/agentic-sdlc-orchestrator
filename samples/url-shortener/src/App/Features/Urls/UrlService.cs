using App.Platform.Caching;

namespace App.Features.Urls;

public interface IUrlService
{
    Task<CreateResult> CreateAsync(CreateUrlRequest request, CancellationToken ct);
    /// <summary>Hot path for redirects: served from cache, falls back to the database; enforces expiry.</summary>
    Task<ResolveResult> ResolveAsync(string code, CancellationToken ct);
    Task<ShortUrl?> GetAsync(string code, CancellationToken ct);
    Task<UrlStatsResponse?> GetStatsAsync(string code, int days, CancellationToken ct);
}

public sealed class UrlService(
    IUrlRepository urls,
    IClickAnalyticsRepository analytics,
    IShortCodeGenerator generator,
    ICacheService cache,
    TimeProvider time,
    ILogger<UrlService> logger) : IUrlService
{
    public const int MaxGenerateAttempts = 5;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<CreateResult> CreateAsync(CreateUrlRequest request, CancellationToken ct)
    {
        var errors = UrlValidator.Validate(request);
        if (errors.Count > 0) return new CreateResult(CreateStatus.Invalid, Errors: errors);

        var expiresAt = request.ExpiresInMinutes is { } minutes ? time.GetUtcNow().AddMinutes(minutes) : (DateTimeOffset?)null;

        if (request.CustomAlias is { } alias)
        {
            var custom = NewUrl(alias, request.Url, expiresAt);
            return await urls.TryAddAsync(custom, ct)
                ? await CachedAsync(custom, ct)
                : new CreateResult(CreateStatus.AliasTaken);
        }

        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            var url = NewUrl(generator.Generate(), request.Url, expiresAt);
            if (await urls.TryAddAsync(url, ct)) return await CachedAsync(url, ct);
            logger.LogWarning("Short code collision on attempt {Attempt}", attempt);
        }
        throw new InvalidOperationException($"Could not allocate a unique short code after {MaxGenerateAttempts} attempts.");
    }

    public async Task<ResolveResult> ResolveAsync(string code, CancellationToken ct)
    {
        var link = await cache.GetOrCreateAsync(CacheKey(code), async token =>
            await urls.GetByCodeAsync(code, token) is { } url ? new CachedLink(url.TargetUrl, url.ExpiresAt) : null, CacheTtl, ct);

        if (link is null) return ResolveResult.NotFound;
        // Expiry is checked on every request (cache hits included), so a cached entry can never outlive its link.
        if (link.ExpiresAt is { } expires && expires <= time.GetUtcNow()) return ResolveResult.Expired;
        return new ResolveResult(ResolveStatus.Found, link.TargetUrl);
    }

    public Task<ShortUrl?> GetAsync(string code, CancellationToken ct) => urls.GetByCodeAsync(code, ct);

    public async Task<UrlStatsResponse?> GetStatsAsync(string code, int days, CancellationToken ct)
    {
        var url = await urls.GetByCodeAsync(code, ct);
        if (url is null) return null;
        var since = time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 365));
        return await analytics.GetStatsAsync(code, url.ClickCount, since, ct);
    }

    private ShortUrl NewUrl(string code, string target, DateTimeOffset? expiresAt) =>
        new() { Code = code, TargetUrl = target, CreatedAt = time.GetUtcNow(), ExpiresAt = expiresAt };

    private async Task<CreateResult> CachedAsync(ShortUrl url, CancellationToken ct)
    {
        await cache.SetAsync(CacheKey(url.Code), new CachedLink(url.TargetUrl, url.ExpiresAt), CacheTtl, ct);
        return new CreateResult(CreateStatus.Created, url);
    }

    private static string CacheKey(string code) => $"url:{code}";
}
