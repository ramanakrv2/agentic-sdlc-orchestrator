using App.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Features.Urls;

public interface IUrlRepository
{
    /// <returns>False when the code already exists (unique constraint).</returns>
    Task<bool> TryAddAsync(ShortUrl url, CancellationToken ct);
    Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct);
}

public sealed class EfUrlRepository(AppDbContext db, IDbExecutor executor) : IUrlRepository
{
    public async Task<bool> TryAddAsync(ShortUrl url, CancellationToken ct)
    {
        try
        {
            await executor.ExecuteAsync(async token =>
            {
                db.Set<ShortUrl>().Add(url);
                await db.SaveChangesAsync(token);
            }, ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await ExistsAsync(url.Code, ct)) return false;
            throw;
        }
    }

    public Task<ShortUrl?> GetByCodeAsync(string code, CancellationToken ct) =>
        db.Set<ShortUrl>().AsNoTracking().FirstOrDefaultAsync(u => u.Code == code, ct);

    private Task<bool> ExistsAsync(string code, CancellationToken ct) =>
        db.Set<ShortUrl>().AsNoTracking().AnyAsync(u => u.Code == code, ct);
}
