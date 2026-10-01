using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Features.Urls;

/// <summary>A shortened link. <see cref="Code"/> is unique and is the public identifier.</summary>
public sealed class ShortUrl
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Denormalised click total, incremented in batches by the analytics worker.</summary>
    public long ClickCount { get; set; }
    /// <summary>Optional expiry; after this instant the link no longer redirects (410 Gone).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;
}

public sealed class ShortUrlConfiguration : IEntityTypeConfiguration<ShortUrl>
{
    public void Configure(EntityTypeBuilder<ShortUrl> builder)
    {
        builder.ToTable("short_urls");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.TargetUrl).HasMaxLength(2048).IsRequired();
    }
}
