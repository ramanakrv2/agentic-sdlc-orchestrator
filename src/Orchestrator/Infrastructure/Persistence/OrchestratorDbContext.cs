using Microsoft.EntityFrameworkCore;

namespace Orchestrator.Infrastructure.Persistence;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<NodeStateEntity> NodeStates => Set<NodeStateEntity>();
    public DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RunEntity>(e =>
        {
            e.HasKey(x => x.RunId);
            e.Property(x => x.RunId).HasMaxLength(64);
            e.HasIndex(x => x.StartedAt);
        });
        b.Entity<NodeStateEntity>(e =>
        {
            e.HasKey(x => new { x.RunId, x.NodeId });
            e.Property(x => x.NodeId).HasMaxLength(200);
        });
        b.Entity<ArtifactEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.Key, x.Version }).IsUnique();
        });
        b.Entity<EventEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.Timestamp });
            e.HasIndex(x => x.Type);
        });
        b.Entity<AuditEntity>(e =>
        {
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).ValueGeneratedNever();
            e.HasIndex(x => x.RunId);
        });
    }
}

public sealed class RunEntity
{
    public string RunId { get; set; } = "";
    public string Requirement { get; set; } = "";
    public string? ProjectSlug { get; set; }
    public string Status { get; set; } = "";
    public string? StatusReason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RetriesUsed { get; set; }
    public string SettingsJson { get; set; } = "{}";
}

public sealed class NodeStateEntity
{
    public string RunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public string? InputFingerprint { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string PendingFeedbackJson { get; set; } = "[]";
    public bool UseFallback { get; set; }
}

public sealed class ArtifactEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string Key { get; set; } = "";
    public int Version { get; set; }
    public string Content { get; set; } = "";
    public string Hash { get; set; } = "";
    public string ProducedBy { get; set; } = "";
    public string InputFingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EventEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string? NodeId { get; set; }
    public string? Stage { get; set; }
    public double? DurationMs { get; set; }
    public string? DataJson { get; set; }
}

public sealed class AuditEntity
{
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string RunId { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";
}
