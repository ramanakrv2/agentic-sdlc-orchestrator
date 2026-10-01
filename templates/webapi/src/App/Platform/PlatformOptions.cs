namespace App.Platform;

// PLATFORM-OWNED (template-locked).

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    /// <summary>Sqlite (default, local) | Postgres. Switching is configuration-only.</summary>
    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=data/app.db";
    /// <summary>Development convenience: recreate the database when the EF model changes (production uses migrations).</summary>
    public bool ResetOnModelChange { get; set; } = true;
}

public sealed class CacheOptions
{
    public const string Section = "Cache";
    public bool Enabled { get; set; } = true;
    /// <summary>Memory (per instance) | Redis (shared across instances). Switching is configuration-only.</summary>
    public string Provider { get; set; } = "Memory";
    public string RedisConfiguration { get; set; } = "localhost:6379";
    public int DefaultTtlSeconds { get; set; } = 300;
    public BreakerOptions CircuitBreaker { get; set; } = new();

    public sealed class BreakerOptions
    {
        public double FailureRatio { get; set; } = 0.5;
        public int MinimumThroughput { get; set; } = 5;
        public int SamplingSeconds { get; set; } = 10;
        public int BreakSeconds { get; set; } = 15;
    }
}

public sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";
    public bool Enabled { get; set; } = true;
    public int PermitLimit { get; set; } = 200;
    public int WindowSeconds { get; set; } = 10;
}

public sealed class RequestOptions
{
    public const string Section = "Requests";
    public int TimeoutMs { get; set; } = 5000;
}

/// <summary>Fault injection for resilience demos. Never enable in production.</summary>
public sealed class ChaosOptions
{
    public const string Section = "Chaos";
    public double CacheFaultRate { get; set; }
    public int DbLatencyMs { get; set; }
}
