namespace App.Features.Urls;

/// <param name="Url">Absolute http(s) URL to shorten.</param>
/// <param name="CustomAlias">Optional vanity code (4-32 chars: letters, digits, '-' or '_').</param>
/// <param name="ExpiresInMinutes">Optional lifetime in minutes (1 to 525600 = one year). Null = never expires.</param>
public sealed record CreateUrlRequest(string Url, string? CustomAlias = null, int? ExpiresInMinutes = null);

public sealed record UrlResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, long ClickCount, DateTimeOffset? ExpiresAt = null);

public sealed record DailyClicks(DateOnly Date, long Clicks);

public sealed record ReferrerClicks(string Referrer, long Clicks);

public sealed record UrlStatsResponse(
    string Code,
    long TotalClicks,
    IReadOnlyList<DailyClicks> Daily,
    IReadOnlyList<ReferrerClicks> TopReferrers,
    IReadOnlyDictionary<string, long> UserAgents);

/// <summary>Outcome of creating a link; endpoints map it to HTTP status codes.</summary>
public enum CreateStatus { Created, Invalid, AliasTaken }

public sealed record CreateResult(CreateStatus Status, ShortUrl? Url = null, IDictionary<string, string[]>? Errors = null);

/// <summary>Outcome of resolving a code on the redirect path.</summary>
public enum ResolveStatus { Found, Expired, NotFound }

public sealed record ResolveResult(ResolveStatus Status, string? TargetUrl = null)
{
    public static readonly ResolveResult NotFound = new(ResolveStatus.NotFound);
    public static readonly ResolveResult Expired = new(ResolveStatus.Expired);
}

/// <summary>What the redirect path caches per code: the target and its expiry (so expiry is enforced on cache hits too).</summary>
public sealed record CachedLink(string TargetUrl, DateTimeOffset? ExpiresAt);
