using App.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Features.Urls;

public interface IClickAnalyticsRepository
{
    /// <summary>Stores a batch of clicks and increments the per-link totals in one transaction.</summary>
    Task AddBatchAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken ct);
    Task<UrlStatsResponse> GetStatsAsync(string code, long totalClicks, DateTimeOffset since, CancellationToken ct);
}

public sealed class EfClickAnalyticsRepository(AppDbContext db) : IClickAnalyticsRepository
{
    public async Task AddBatchAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Set<ClickEvent>().AddRange(clicks);
        await db.SaveChangesAsync(ct);
        foreach (var group in clicks.GroupBy(c => c.Code))
        {
            var count = group.LongCount();
            await db.Set<ShortUrl>().Where(u => u.Code == group.Key)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ClickCount, u => u.ClickCount + count), ct);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<UrlStatsResponse> GetStatsAsync(string code, long totalClicks, DateTimeOffset since, CancellationToken ct)
    {
        // Filtered by code in SQL, windowed in memory: SQLite cannot translate DateTimeOffset comparisons.
        var events = (await db.Set<ClickEvent>().AsNoTracking()
                .Where(c => c.Code == code)
                .Select(c => new { c.OccurredAt, c.ReferrerHost, c.UserAgentFamily })
                .ToListAsync(ct))
            .Where(c => c.OccurredAt >= since)
            .ToList();

        var daily = events.GroupBy(e => DateOnly.FromDateTime(e.OccurredAt.UtcDateTime))
            .OrderBy(g => g.Key).Select(g => new DailyClicks(g.Key, g.LongCount())).ToList();
        var referrers = events.GroupBy(e => e.ReferrerHost ?? "direct")
            .OrderByDescending(g => g.Count()).Take(10).Select(g => new ReferrerClicks(g.Key, g.LongCount())).ToList();
        var agents = events.GroupBy(e => e.UserAgentFamily).ToDictionary(g => g.Key, g => g.LongCount());

        return new UrlStatsResponse(code, totalClicks, daily, referrers, agents);
    }
}
