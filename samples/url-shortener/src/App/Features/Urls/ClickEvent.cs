using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Features.Urls;

/// <summary>One redirect. No IP address is stored (privacy by design); referrer host and user-agent family only.</summary>
public sealed class ClickEvent
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string? ReferrerHost { get; set; }
    public string UserAgentFamily { get; set; } = "Other";
}

public sealed class ClickEventConfiguration : IEntityTypeConfiguration<ClickEvent>
{
    public void Configure(EntityTypeBuilder<ClickEvent> builder)
    {
        builder.ToTable("click_events");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ReferrerHost).HasMaxLength(255);
        builder.Property(x => x.UserAgentFamily).HasMaxLength(32);
        builder.HasIndex(x => new { x.Code, x.OccurredAt });
    }
}
