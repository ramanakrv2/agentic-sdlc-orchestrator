using App.Platform.Queue;

namespace App.Features.Urls;

/// <summary>Writes queued click events in batches, off the redirect path.</summary>
public sealed class ClickRecorder(IClickAnalyticsRepository analytics) : IBatchHandler<ClickEvent>
{
    public Task HandleAsync(IReadOnlyList<ClickEvent> batch, CancellationToken ct) => analytics.AddBatchAsync(batch, ct);

    public static string UserAgentFamily(string? userAgent) => userAgent switch
    {
        null or "" => "Unknown",
        _ when userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) => "Bot",
        _ when userAgent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
        _ when userAgent.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
        _ when userAgent.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
        _ when userAgent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
        _ when userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase) => "curl",
        _ => "Other",
    };

    public static string? ReferrerHost(string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) ? uri.Host : null;
}
